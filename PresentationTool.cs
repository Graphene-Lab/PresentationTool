using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using HtmlAgilityPack;
using SixLabors.ImageSharp;
using System.Diagnostics;
using UISupportGeneric;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PresentationTool.Harness")]

namespace AIOrchestrator.API
{
    /// <summary>Create and edit slide presentations as self-contained HTML decks (browser-viewable, not PowerPoint files).
    /// Built from a description or a change request. For real .pptx files use OfficeTool. File paths are Unix-style, relative to
    /// the workspace root — never escape it. Every saved deck is guaranteed to be a real slide show (one slide at a time,
    /// prev/next buttons, counter, fits the window); when a local desktop session is available the finished deck is also
    /// opened in the default browser so the user can watch it.</summary>
    public class PresentationTool : BaseAgentTool, IFileTool, ILocalDesktopCapable
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        /// <summary>Set by the orchestrator right after instantiating this tool (see <see cref="ILocalDesktopCapable"/>):
        /// true only when a real local user sits at an interactive desktop session. It gates the "open the deck in the
        /// browser" delivery — on a headless/server run no window may ever be raised.</summary>
        public bool IsWatchAllowed { get; set; }
        private static readonly string[] Styles = ["Modern", "Vintage", "Minimalist White", "Brutalist", "Retro Pop", "Vaporwave", "Biophilic Design", "Cyberpunk Neon", "Glassmorphism", "Bento Grid", "Retro"];
        private const int MaxHtmlAttempts = 3;
        private const string OnlyOutputAnswer = "- Output only full HTML code. No opening or closing comments, no fences. [Output only]";

        /// <summary>Registers the resolver for the [[available_styles]] dynamic placeholder: the tool
        /// description (param "style") always shows the current style list, in sync with the code.</summary>
        static PresentationTool()
        {
            Analyzer.DynamicDescriptionRequested += (_, e) =>
            {
                if (e.ToolType == typeof(PresentationTool) && e.Placeholder == "available_styles")
                    e.Value = string.Join(", ", Styles);
            };
        }

        /// <summary>Generate a PowerPoint-style presentation</summary>
        /// <param name="description">What the presentation must cover. The description must include: the subject, a descriptive title and the purpose of the presentation (e.g. "Present the Q3 2026 sales results, titled 'Record Quarter', to the management team — 5 slides"). Keep it a guideline: put the supporting material in contextText or contextFile, otherwise the tool rejects the request for lack of material.</param>
        /// <param name="style">Optional graphic style that shapes the deck. Available styles: [[available_styles]].</param>
        /// <param name="outputTwoLetterLanguage">Optional two-letter language code for the presentation (e.g. "en", "fr"); if omitted, the context data language is used.</param>
        /// <param name="contextText">Optional context text the deck content must be based on. (Mandatory if contextFile is missing)</param>
        /// <param name="contextFile">Optional workspace file read as content context (Unix-style path, e.g. "/docs/report.md"). (Mandatory if contextText is missing)</param>
        /// <param name="imageFiles">Optional workspace image files to place in the deck (Unix-style paths, e.g. "/images/chart.png"). Each image is used at most once.</param>
        /// <param name="saveFullNameFile">Optional output file path and name (Unix-style, must end with ".html", e.g. "/out/sales.html"). Default: "/presentation/presentation_yyyyMMdd_HHmmss.html" in the workspace.</param>
        /// <returns>The generated .html path in workspace form, or an "Error: ..." message (missing input, unsupported image type, insufficient context, unclear description, LLM failure).</returns>
        public string CreatePresentation(string description, string? style = null, string? contextText = null, string? contextFile = null, string[]? imageFiles = null, string? saveFullNameFile = null, string? outputTwoLetterLanguage = null)
        {
            if (string.IsNullOrWhiteSpace(description))
                return "Error: description is required.";
            if (saveFullNameFile != null && !saveFullNameFile.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                return "Error: saveFullNameFile must end with '.html' (the presentation is saved as a self-contained HTML file).";

            string hostPath;
            try
            {
                hostPath = SandboxPath.Resolve(saveFullNameFile
                    ?? $"/presentation/presentation_{DateTime.Now:yyyyMMdd_HHmmss}.html");
            }
            catch (UnauthorizedAccessException ex) { return $"Error: {ex.Message}"; }
            Directory.CreateDirectory(Path.GetDirectoryName(hostPath)!);

            var context = new StringBuilder();
            var contextFiles = new List<string>();
            if (!string.IsNullOrWhiteSpace(contextText)) context.AppendLine(contextText);
            if (!string.IsNullOrWhiteSpace(contextFile))
            {
                string ctxHost;
                try { ctxHost = SandboxPath.Resolve(contextFile); }
                catch (UnauthorizedAccessException ex) { return $"Error: {ex.Message}"; }
                if (!File.Exists(ctxHost)) return $"Error: context file '{contextFile}' not found in the workspace.";
                contextFiles.Add(SandboxPath.ToAgent(ctxHost));
                context.AppendLine(ReadTextCapped(ctxHost, 60_000));
            }

            var (images, imagesError) = ResolveImages(imageFiles);
            if (imagesError != null) return imagesError;

            style ??= Styles[Random.Shared.Next(Styles.Length)];
            Log.LogStep($"PresentationTool.CreatePresentation: description='{Truncate(description, 120)}' style={style} images={images!.Count} contextLen={context.Length}");

            var opinion = AskOpinion(description, context.ToString());
            if (opinion == null) return "Error: the LLM returned no usable evaluation of the request. Retry later.";
            if (!opinion.Sufficient || !opinion.DescriptionClear)
                return BuildInsufficientError(opinion);

            var html = GenerateHtml(BuildCreatePrompt(description, style, contextFiles, context.ToString(), images, outputTwoLetterLanguage));
            if (html == null) return $"Error: the LLM returned no usable HTML after {MaxHtmlAttempts} attempts. Retry later.";
            html = EnsureImagesUsed(html, images);
            var improved = GenerateHtml(ImproveHtmlCode(html, style));
# if DEBUG
            if (improved == null)
                Debugger.Break();
#endif
            if (improved != null) html = improved;
            else Log.LogStep("PresentationTool.CreatePresentation: styling pass failed, keeping first-pass HTML");
            var checkedHtml = GenerateHtml(BuildCheckFixPrompt(html));
            if (checkedHtml != null) html = checkedHtml;
            else Log.LogStep("PresentationTool.CreatePresentation: check&fix pass failed, keeping previous HTML");
            html = EnsureImagesUsed(html, images); // re-check: later passes may have dropped the images
            html = EmbedImages(html, images);
            html = EmbedSvgIcons(html);
            html = InjectAnimatedBackground(html, style);
            // Last step on purpose: no matter what the passes above did to the template (a styling pass
            // that rewrites the page, a model that returns its own document instead of filling the
            // template's .slide elements), what reaches the disk is always a real slide show.
            html = EnsureDeckScaffold(html);

            string? versionId = null;
            try
            {
                File.WriteAllText(hostPath, html);
                versionId = GitSupport.Snapshot(hostPath, "PresentationTool create");
            }
            catch (Exception ex)
            {
                Log.LogStep($"PresentationTool.CreatePresentation: failed '{hostPath}': {ex}");
                return "Error: cannot save the presentation (write failed). Retry later.";
            }
            Log.LogStep($"PresentationTool.CreatePresentation: wrote '{hostPath}' ({html.Length} chars) version='{versionId}'");
            OpenDeck(hostPath);
            return versionId != null
                ? $"Presentation created at {SandboxPath.ToAgent(hostPath)}. New version: {versionId}. (Rollback via GitTool.restore.)"
                : $"Presentation created at {SandboxPath.ToAgent(hostPath)}.";
        }

        /// <summary>Updates an existing presentation on request (e.g. change a slide, recolor the deck, add or remove content): the requested changes are applied and the file is overwritten in place. The new content becomes a new version (rollback via GitTool.restore).</summary>
        /// <param name="filePath">Path of the presentation to update (Unix-style, e.g. "/presentation/sales.html").</param>
        /// <param name="changes">The changes to apply (e.g. "shorten slide 3, change the colors, add a summary slide at the end").</param>
        /// <param name="contextText">Optional extra context the update must respect.</param>
        /// <param name="imageFiles">Optional workspace image files the update must place in the deck (Unix-style paths, e.g. "/images/chart.png"), same semantics as in CreatePresentation: each image is used at most once.</param>
        /// <returns>The updated .html path in workspace form (with the backup name), or an "Error: ..." message (missing input, unclear changes, LLM failure).</returns>
        public string UpdatePresentation(string filePath, string changes, string? contextText = null, string[]? imageFiles = null)
        {
            if (string.IsNullOrWhiteSpace(changes)) return "Error: changes is required.";
            string hostPath;
            try { hostPath = SandboxPath.Resolve(filePath); }
            catch (UnauthorizedAccessException ex) { return $"Error: {ex.Message}"; }
            if (!File.Exists(hostPath)) return $"Error: file '{filePath}' not found in the workspace.";
            if (!hostPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                return $"Error: only .html presentations can be updated (they are generated by {AIOrchestrator.Utility.ToSnakeCase(nameof(CreatePresentation))}).";

            string currentHtml;
            try { currentHtml = File.ReadAllText(hostPath); }
            catch (Exception ex)
            {
                Log.LogStep($"PresentationTool.UpdatePresentation: cannot read '{hostPath}': {ex}");
                return "Error: cannot read the presentation (the file may be corrupted).";
            }

            var (images, imagesError) = ResolveImages(imageFiles);
            if (imagesError != null) return imagesError;

            Log.LogStep($"PresentationTool.UpdatePresentation: '{hostPath}' changes='{Truncate(changes, 120)}' contextText='{Truncate(contextText ?? "", 120)}' images={images!.Count}");
            var verdict = AskChangesClear(changes, contextText, images);
            if (verdict == null) return "Error: the LLM returned no usable evaluation of the changes. Retry later.";
            if (!verdict.Clear)
                return $"Error: the requested changes are not clear enough to apply. {Reasons(verdict.Explanation, "the changes do not say what to modify")}";

            var html = UpdateHtmlCore(currentHtml, changes, contextText, images);
            if (html == null) return $"Error: the LLM returned no usable HTML after {MaxHtmlAttempts} attempts. Retry later.";
            html = EmbedImages(html, images);
            html = EmbedSvgIcons(html);
            // An update may legitimately rewrite the whole document ("replace the file with this
            // markup"); the deck show must survive that, so the scaffold is re-guaranteed here too
            // instead of only at creation time.
            html = EnsureDeckScaffold(html);

            string? versionId = null;
            try
            {
                File.WriteAllText(hostPath, html);
                versionId = GitSupport.Snapshot(hostPath, "PresentationTool update");
            }
            catch (Exception ex)
            {
                Log.LogStep($"PresentationTool.UpdatePresentation: failed '{hostPath}': {ex}");
                return "Error: cannot apply the changes (write failed). Retry later.";
            }

            Log.LogStep($"PresentationTool.UpdatePresentation: '{hostPath}' updated ({html.Length} chars) version='{versionId}'");
            OpenDeck(hostPath);
            return versionId != null
                ? $"Presentation updated at {SandboxPath.ToAgent(hostPath)}. New version: {versionId}. (Rollback via GitTool.restore.)"
                : $"Presentation updated at {SandboxPath.ToAgent(hostPath)}.";
        }

        // ---------- LLM ----------

        /// <summary>Resolves the optional image files to host paths, validating existence and type.
        /// A path that does not exist is retried as a bare file name (searched across the whole
        /// workspace) — the same tolerance Restore applies to backup names: agents often pass just
        /// the file name instead of the full Unix-style path. Returns (images, null) on success or
        /// (null, error message) on the first bad entry.</summary>
        private static (List<string>? Images, string? Error) ResolveImages(string[]? imageFiles)
        {
            var images = new List<string>();
            if (imageFiles == null) return (images, null);
            foreach (var img in imageFiles.Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                string imgHost;
                try { imgHost = SandboxPath.Resolve(img); }
                catch (UnauthorizedAccessException ex) { return (null, $"Error: {ex.Message}"); }
                if (!File.Exists(imgHost))
                {
                    // bare or mis-prefixed path: search the workspace by file name
                    imgHost = Directory.GetFiles(Setup.DocumentsPath, Path.GetFileName(img), SearchOption.AllDirectories)
                        .FirstOrDefault() ?? imgHost;
                }
                if (!File.Exists(imgHost))
                    return (null, $"Error: image file '{img}' not found in the workspace. Pass the file's Unix-style workspace path (e.g. '/images/chart.png').");
                if (MimeFor(imgHost) == null) return (null, $"Error: unsupported image type for '{img}'. Use png, jpg, gif, bmp, svg or webp.");
                images.Add(imgHost);
            }
            return (images, null);
        }

        /// <summary>Checks the first-pass deck for each context image by searching the file name as a
        /// string; any image the LLM did not reference is forced into the deck through the update flow
        /// (the changes text instructs to insert them where most appropriate).</summary>
        private static string EnsureImagesUsed(string html, List<string> images)
        {
            if (images.Count == 0) return html;
            var missing = images.Where(i => !html.Contains(Path.GetFileName(i), StringComparison.OrdinalIgnoreCase)).ToList();
            if (missing.Count == 0)
            {
                Log.LogStep("PresentationTool.CreatePresentation: all context images referenced in the first pass");
                return html;
            }
            Log.LogStep($"PresentationTool.CreatePresentation: forcing {missing.Count} missing image(s) via update flow: " +
                        string.Join(", ", missing.Select(Path.GetFileName)));
            const string changes = "These images are part of the presentation and should be inserted into the html page where most appropriate.";
            return UpdateHtmlCore(html, changes, null, images) ?? html;
        }

        /// <summary>Shared LLM core of the update flow: generates the updated deck from the current
        /// HTML plus the requested changes, then runs the check&amp;fix pass. Returns null when the
        /// LLM cannot produce valid HTML. Embedding (images/icons) is left to the callers.</summary>
        private static string? UpdateHtmlCore(string currentHtml, string changes, string? contextText, List<string> images)
        {
            var html = GenerateHtml(BuildUpdatePrompt(currentHtml, changes, contextText, images));
            if (html == null) return null;
            var checkedHtml = GenerateHtml(BuildCheckFixPrompt(html));
            if (checkedHtml != null) html = checkedHtml;
            else Log.LogStep("PresentationTool.UpdatePresentation: check&fix pass failed, keeping updated HTML");
            return html;
        }

        /// <summary>Asks the LLM (no history) whether the given context and description are enough to build the deck; returns the JSON verdict or null when the LLM fails.</summary>
        private static SufficiencyOpinion? AskOpinion(string description, string context)
        {
            using var llm = new LLMUtility(Setup.ProviderConfig.ProviderName);
            var prompt = $$"""
                Today's date: {{DateTime.Now:yyyy-MM-dd}}

                You check whether the material provided is sufficient to fulfill the presentation request.
                Check if the context material is sufficient to create a presentation as specified in the description.

                Description of the task to be performed (description):
                ```text
                {{(string.IsNullOrWhiteSpace(description) ? "(none provided)" : description)}}
                ```

                Material (context):
                ```text
                {{(string.IsNullOrWhiteSpace(context) ? "(none provided)" : context)}}
                ```

                Respond with ONLY JSON (no fences, no commentary):
                {"sufficient": true|false, "descriptionClear": true|false, "explanation": ["what is missing or unclear", ...]}
                - "sufficient" = false when the background material is not sufficient for a powerpoint presentation that fulfills the description's requirements.
                - "descriptionClear" = false when the description is ambiguous (unclear subject, title or purpose).
                - "explanation" lists the concrete missing/unclear items when a flag is false; empty otherwise.
                """;
            var (response, hResult) = llm.SendQuery(prompt, useHistory: false, role: LLMUtility.SystemRole.DocumentPreparer,
                forceJsonResponse: true);
            if (hResult != null || string.IsNullOrWhiteSpace(response)) return null;
            var opinion = TryParseJson<SufficiencyOpinion>(response);
            if (opinion == null) Log.LogStep($"PresentationTool.AskOpinion: unparseable JSON response");
            else Log.LogStep($"PresentationTool.AskOpinion: sufficient={opinion.Sufficient} descriptionClear={opinion.DescriptionClear}");
            return opinion;
        }

        /// <summary>Asks the LLM (no history) whether the requested changes (and optional context)
        /// are clear enough to apply. The provided images are made known to the evaluator: a request
        /// that refers to an available image (e.g. "add the logo") is clear — without this the gate
        /// would reject it as ambiguous. Returns the JSON verdict or null when the LLM fails.</summary>
        private static ChangeVerdict? AskChangesClear(string changes, string? contextText, List<string> images)
        {
            using var llm = new LLMUtility(Setup.ProviderConfig.ProviderName);
            var prompt = $$"""
                Today's date: {{DateTime.Now:yyyy-MM-dd}}

                You are about to edit an existing presentation. Validate that the requested changes are clear enough to apply.
                Rules:
                - Decide sensible details yourself (e.g. which slide is "the first", which shade of blue, what "colors" means) — these do NOT make the request unclear.
                - Reject ONLY when the changes are genuinely unusable: empty, meaningless or contradictory requests.
                {{(!string.IsNullOrWhiteSpace(contextText) ? "Additional context: " + contextText : "")}}
                {{(images.Count > 0 ? "Available images (the request may refer to them by file name): " + string.Join(", ", images.Select(Path.GetFileName)) : "")}}

                Requested changes: {{changes}}

                Respond with ONLY JSON (no fences, no commentary):
                {"clear": true|false, "explanation": ["what is missing or unclear", ...]}
                - "clear" = false ONLY when the changes cannot be interpreted at all.
                - "explanation" lists what is unclear when "clear" is false; empty array otherwise.
                """;
            var (response, hResult) = llm.SendQuery(prompt, useHistory: false, role: LLMUtility.SystemRole.DocumentPreparer,
                forceJsonResponse: true);
            if (hResult != null || string.IsNullOrWhiteSpace(response)) return null;
            var verdict = TryParseJson<ChangeVerdict>(response);
            if (verdict == null) Log.LogStep($"PresentationTool.AskChangesClear: unparseable JSON response");
            else Log.LogStep($"PresentationTool.AskChangesClear: clear={verdict.Clear}");
            return verdict;
        }

        /// <summary>Generates deck HTML via the LLM (no history), validates it as HTML5 and retries up to <see cref="MaxHtmlAttempts"/> times, feeding back the validation errors. Returns null when all attempts fail.</summary>
        private static string? GenerateHtml(string prompt)
        {
            using var llm = new LLMUtility(Setup.ProviderConfig.ProviderName);
            for (int attempt = 1; attempt <= MaxHtmlAttempts; attempt++)
            {
                Log.LogStep($"PresentationTool.GenerateHtml: attempt {attempt}/{MaxHtmlAttempts}");
                var (response, hResult) = llm.SendQuery(prompt, useHistory: false, role: LLMUtility.SystemRole.DocumentPreparer);
                if (hResult != null)
                {
                    Log.LogStep($"PresentationTool.GenerateHtml: LLM error hResult={hResult} on attempt {attempt} — retrying");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(response)) continue;
                var html = response;
                if (!Utility.RemoveFencesEncapsulationAndFixTrim(ref html, false))
                {
                    Log.LogStep($"PresentationTool.GenerateHtml: malformed fences on attempt {attempt}");
                    continue;
                }
                if (IsValidHtml5(html, out var errors))
                {
                    Log.LogStep($"PresentationTool.GenerateHtml: valid HTML on attempt {attempt}");
                    return html;
                }
                Log.LogStep($"PresentationTool.GenerateHtml: invalid HTML on attempt {attempt} ({errors.Count} errors: {string.Join(" | ", errors.Take(6))})");
                if (attempt == MaxHtmlAttempts) break;
                var errorFeedback = string.Join("\n", errors.Take(8).Select(e => $"  - {e}"));
                var missingEnd = !html.TrimEnd().EndsWith("</html>", StringComparison.OrdinalIgnoreCase);
                var truncated = missingEnd && html.Length > 20000;
                prompt = $"""
                    The previous HTML5 code you provided was not valid.
                    Here is the code that failed:
                    ```html
                    {html}
                    ```
                    Validation errors:
                    {errorFeedback}
                    {(truncated ? "The output was truncated by the output limit: the closing tags are missing at the end of the document. Produce a MORE COMPACT version that fits in the output limit: keep ALL the content but compress the markup (shorter inline styles, fewer wrapper elements). The output MUST end with the closing </html> tag." : missingEnd ? "The document does not end with the closing </html> tag." : "")}

                    Please fix ALL the errors above and provide a corrected, valid HTML5 version.
                    {OnlyOutputAnswer}
                    """;
            }
            return null;
        }

        private static string BuildCreatePrompt(string description, string style, List<string> contextFiles, string context, List<string> images, string? outputTwoLetterLanguage = null)
        {

            // auto detect language if not specified: from the context, else from the first context file
            if (string.IsNullOrWhiteSpace(outputTwoLetterLanguage))
            {
                outputTwoLetterLanguage = Utility.DetectLanguage(context);
                if (outputTwoLetterLanguage == null && contextFiles.Count > 0)
                {
                    try { outputTwoLetterLanguage = Utility.DetectLanguage(ReadTextCapped(SandboxPath.Resolve(contextFiles[0]), 60_000)); }
                    catch (UnauthorizedAccessException) { }
                }
                outputTwoLetterLanguage ??= "en";
            }
            string languageName;
            try { languageName = new CultureInfo(outputTwoLetterLanguage).EnglishName; }
            catch (CultureNotFoundException) { languageName = "English"; }


            var sb = new StringBuilder();
            sb.AppendLine("Today's date: " + DateTime.Now.ToString("yyyy-MM-dd"));
            sb.AppendLine($"Create a website of presentation in {languageName} using these context documents.");
            sb.AppendLine("Presentation description:");
            sb.AppendLine("```text");
            sb.AppendLine(description);
            sb.AppendLine("```");
            sb.AppendLine("Create as many slides as needed for your purpose (add or remove .slide elements in the template as needed).");
            if (contextFiles.Count > 0)
            {
                sb.AppendLine("Context documents (workspace paths):");
                foreach (var p in contextFiles) sb.AppendLine("- " + p);
            }
            if (!string.IsNullOrWhiteSpace(context))
            {
                sb.AppendLine("Context content:");
                sb.AppendLine("```text");
                sb.AppendLine(context.TrimEnd());
                sb.AppendLine("```");
            }
            sb.AppendLine();
            sb.AppendLine("- To make communication more effective you can insert into the slides: Tables, Kanban Boards, Timelines, Roadmaps, Organizational Charts, Flowcharts, Venn Diagrams, SWOT Analysis grids, PESTLE Analysis frameworks, Decision Trees, and other useful elements.");
            sb.AppendLine("- Don't use small fonts.");
            if (images.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Available images (reference by file name only, e.g. <img src=\"chart.png\">):");
                sb.AppendLine(FileManager.GetFilesInfo(images.Select(SandboxPath.ToAgent)));
                sb.AppendLine("- Use each image once");
                sb.AppendLine();
            }
            sb.AppendLine("- Use square SVG icons with a self-explanatory file name that can encode size and color: <icon-name>.<size>.<rrggbb>.svg (these files will be auto-generated based on the name you give them). Usage example: disc.32.aa0000.svg (a disc icon, 32x32 px, hex color #aa0000) → <img src=\"disc.32.aa0000.svg\" alt=\"disc\">");
            sb.AppendLine($"- Use this template with a \"{style.ToLower()}\" style (keep its CSS classes, light and dark theme, navigation buttons and script unchanged; fill the .slide elements with the deck slides):");
            sb.AppendLine("```html");
            sb.AppendLine(TemplateHtml);
            sb.AppendLine("```");
            sb.AppendLine("- Write the content in the language of the description.");
            sb.AppendLine("- The output MUST be in HTML format");
            sb.AppendLine("- Check before output");
            sb.AppendLine(OnlyOutputAnswer);
            return sb.ToString();
        }

        private static string ImproveHtmlCode(string currentHtml, string style)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Improve HTML code with these enhancements:");
            sb.AppendLine($"- Implement a \"{style.ToUpper()}\" design on the page");
            sb.AppendLine("- Add effects and transitions to slide elements");
            sb.AppendLine("- Use JavaScript to create amazing slide graphics.");
            sb.AppendLine("- MANDATORY, never trade it for looks: the output must stay a slide show — exactly one slide visible at a time inside the 16:9 frame (#presentation-frame / .frame-16-9), with the prev/next buttons, the slide counter and the navigation script still present and working. Do not turn the deck into a long scrolling page and do not remove the frame or the navigation.");
            sb.AppendLine("HTML code:");
            sb.AppendLine("```html");
            sb.AppendLine(currentHtml);
            sb.AppendLine("```");
            sb.AppendLine(OnlyOutputAnswer);
            return sb.ToString();
        }

        private static string BuildCheckFixPrompt(string currentHtml)
        {
            var sb = new StringBuilder();
            sb.AppendLine("* Check and fix the following:");
            sb.AppendLine("- There should be no small fonts.");
            sb.AppendLine("- Content must fit the slide container: (Verify the dimensions mathematically, and fix the content if it goes off the slide).");
            sb.AppendLine("- Check for both light and dark theme the correctness of the contrast between the text color and the background (fix if necessary).");
            sb.AppendLine("- Keep the deck a deck: the 16:9 frame (.frame-16-9 / #presentation-frame), the prev/next navigation buttons, the slide counter and the navigation script must survive your fix, and exactly one slide must carry the \"active\" class.");
            sb.AppendLine("HTML code:");
            sb.AppendLine("```html");
            sb.AppendLine(currentHtml);
            sb.AppendLine("```");
            sb.AppendLine(OnlyOutputAnswer);
            return sb.ToString();
        }

        private static string BuildUpdatePrompt(string currentHtml, string changes, string? contextText, List<string> images)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Today's date: " + DateTime.Now.ToString("yyyy-MM-dd"));
            sb.AppendLine("Edit an existing PowerPoint presentation (16:9 deck) rendered as a single HTML file.");
            sb.AppendLine("Apply the requested changes LITERALLY to the HTML below:");
            sb.AppendLine("- The exact strings in the changes request (titles, labels, text) MUST appear verbatim in the output — do not reword or replace them.");
            sb.AppendLine("- Change ONLY what the changes request; keep the rest of the content, wording and structure identical.");
            sb.AppendLine("- Keep the template's CSS classes, the language, navigation buttons and script unchanged; edit the .slide elements (and styles only if needed).");
            sb.AppendLine("- Even when the request is \"replace the whole file with this markup\", the result must remain a slide show: slides as .slide elements inside the 16:9 frame, exactly one of them .active, prev/next buttons, counter and navigation script present. Never emit a page where the slides simply stack one after the other.");
            sb.AppendLine();
            sb.AppendLine("Current presentation HTML:");
            sb.AppendLine("```html");
            sb.AppendLine(currentHtml);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("Requested changes: " + changes);
            if (images.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Available images (reference by file name only, e.g. <img src=\"chart.png\">):");
                sb.AppendLine(FileManager.GetFilesInfo(images.Select(SandboxPath.ToAgent)));
                sb.AppendLine("- Use each image once");
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(contextText)) sb.AppendLine("Additional context: " + contextText);
            sb.AppendLine("You may add SVG icons with a minimalist self-descriptive file name, such as <icon-name>.svg (these files will be auto-generated based on the minimalist name you give them).");
            sb.AppendLine("Write the content in the language of the presentation.");
            sb.AppendLine(OnlyOutputAnswer);
            return sb.ToString();
        }

        private static string BuildInsufficientError(SufficiencyOpinion opinion)
        {
            var reasons = Reasons(opinion.Explanation, "the context does not cover the requested topic");
            if (!opinion.Sufficient && !opinion.DescriptionClear)
                return $"Error: the context is not sufficient and the description is not clear enough to create the presentation. {reasons}";
            if (!opinion.Sufficient)
                return $"Error: the context is not sufficient to create the presentation. {reasons}";
            return $"Error: the description is not clear enough to create the presentation. {reasons}";
        }

        private static string Reasons(List<string>? explanation, string fallback) =>
            explanation is { Count: > 0 }
                ? string.Join(" ", explanation.Select(e => "- " + e))
                : "- " + fallback;

        // ---------- HTML post-processing ----------

        /// <summary>Replaces every src reference to a provided image with an inline data URI, so the
        /// deck is self-contained. The reference may be a bare file name or a path ending with it
        /// (src="logo.png", src="./img/logo.png", src="/img/logo.png").</summary>
        private static string EmbedImages(string html, List<string> images)
        {
            foreach (var img in images)
            {
                var name = Path.GetFileName(img);
                var dataUri = "data:" + MimeFor(img) + ";base64," + Convert.ToBase64String(File.ReadAllBytes(img));
                html = Regex.Replace(html,
                    $@"src=[""'](?:[^""'/]*/)*{Regex.Escape(name)}[""']",
                    m => $"src=\"{dataUri}\"", RegexOptions.IgnoreCase);
            }
            return html;
        }

        /// <summary>Replaces every "&lt;icon-name&gt;[.&lt;size&gt;].[.&lt;rrggbb&gt;].svg" img placeholder with the
        /// matching icon from the host assets, encoded as a data URI (shared logic in
        /// Utility.EmbedSvgIcons — same pipeline as the document cover render in MD2PDF). The
        /// reference may be a bare name or a path ending with it.</summary>
        internal static string EmbedSvgIcons(string html)
        {
            var iconsPath = Path.Combine(AppContext.BaseDirectory, "assets", "icons");
            return Utility.EmbedSvgIcons(html, iconsPath);
        }

        /// <summary>Injects the content-size fix script before &lt;/body&gt; (the asset is a complete
        /// HTML snippet, inserted as-is). Missing asset is a no-op: the enhancement is optional and
        /// must never fail the deck creation. Idempotent: a document that already carries it (or that
        /// got the deck runtime below, which fits the slide by itself) is returned untouched, so the
        /// two scalers can never stack on top of each other.</summary>
        internal static string InjectFixContentSizeScript(string html)
        {
            if (html.Contains("const zoom", StringComparison.Ordinal) || html.Contains(DeckScaffoldMarker, StringComparison.Ordinal))
                return html;
            try
            {
                var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "fix-content-size.js"), Encoding.UTF8);
                return html.Replace("</body>", script + "\n</body>", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.LogStep($"PresentationTool.InjectFixContentSizeScript: asset not injected: {ex.Message}");
                return html;
            }
        }

        // ---------- slide-show guarantee (issue #106) ----------

        /// <summary>Marker of the deck runtime, written both in the scaffold style block and in the
        /// inlined script. It is what makes the whole post-processing idempotent: a document that
        /// already carries it is never scaffolded (or scaled) a second time.</summary>
        internal const string DeckScaffoldMarker = "AB-DECK-SCAFFOLD-V1";

        /// <summary>Minimal geometry of a slide show, injected as the FIRST style block of the head so
        /// everything the document itself declares (colours, typography, charts, tables) keeps winning.
        /// Only the structure that must never be lost uses the <c>.frame-16-9 &gt; .slide</c> form, which
        /// outranks a plain <c>.slide</c> rule and therefore cannot be pulled back into normal document
        /// flow by the document's own CSS. The frame is sized on BOTH axes (168.9vh = 95vh × 16/9), so a
        /// window shorter than 16:9 no longer cuts the top and bottom off every slide.</summary>
        private const string DeckScaffoldCss = """
<style>/* AB-DECK-SCAFFOLD-V1 */
html{font-size:min(1.2vw,2.13vh);}
body{display:flex;justify-content:center;align-items:center;min-height:100vh;margin:0;}
.frame-16-9{width:min(95vw,168.9vh);max-width:95vw;aspect-ratio:16/9;background:var(--color-slide-bg,#ffffff40);position:relative;overflow:hidden;border-radius:1.8rem;box-shadow:0 2rem 4rem rgba(0,0,0,.6);margin:0;}
.frame-16-9>.slide{position:absolute;left:0;top:0;margin:0;visibility:hidden;}
.frame-16-9>.slide.active{visibility:visible;}
.nav-btn{position:fixed;top:50%;transform:translateY(-50%);width:3rem;height:3rem;border:none;border-radius:50%;background:var(--color-controls-bg,rgba(0,0,0,.3));color:var(--color-controls-text,#fff);font-size:1.3rem;cursor:pointer;display:flex;align-items:center;justify-content:center;z-index:1000;}
#prev-slide{left:1rem;}
#next-slide{right:1rem;}
#slide-counter{position:fixed;bottom:1rem;left:50%;transform:translateX(-50%);background:var(--color-controls-bg,rgba(0,0,0,.3));color:var(--color-controls-text,#fff);padding:.4rem .8rem;border-radius:1rem;font-size:1.3rem;z-index:1000;pointer-events:none;}
</style>
""";

        /// <summary>The deck runtime (Assets/deck-scaffold.js) inlined into the saved file, so the deck
        /// stays self-contained and needs no asset next to it when somebody opens it elsewhere.</summary>
        private static readonly Lazy<string> DeckRuntimeJsLazy = new(() =>
        {
            var assembly = typeof(PresentationTool).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".deck-scaffold.js", StringComparison.OrdinalIgnoreCase));
            if (name == null) throw new InvalidOperationException("Embedded resource 'Assets/deck-scaffold.js' not found in PresentationTool.");
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null) throw new InvalidOperationException("Embedded resource 'Assets/deck-scaffold.js' not found in PresentationTool.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        internal static string DeckRuntimeJs => DeckRuntimeJsLazy.Value;

        /// <summary>Guarantees that what is saved is a real slide show and not a long scrolling page
        /// that merely looks like one (issue #106: a deck whose slides stack vertically and run off the
        /// window is unreadable and cannot be presented). Behaviour:
        /// - no <c>.slide</c> element at all → returned untouched, there is nothing to turn into a deck;
        /// - a document that already carries the shipped template's frame, controls and navigation
        ///   undamaged → untouched, only the content-size fix is ensured;
        /// - anything else (the model returned its own document, a pass dropped the frame or the
        ///   navigation, a "replace the whole file" update) → every top-level <c>.slide</c> element is
        ///   moved, in document order, into one canonical 16:9 frame; the first slide becomes the active
        ///   one; leftover/partial navigation buttons and nav scripts are replaced by the canonical
        ///   ones; the document's own head (title, meta, styles) is preserved.
        /// Never throws: on any surprise the original HTML is returned, because a readable but plain
        /// document is better than no file at all.</summary>
        internal static string EnsureDeckScaffold(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return html;
            try
            {
                var doc = new HtmlDocument { OptionFixNestedTags = true };
                doc.LoadHtml(html);
                // Force the whole tree to materialize before touching it. LoadHtml keeps a command
                // stream and builds nodes lazily: moving a node that still lives only in the stream
                // emits it twice (its markup from the stream plus the moved node), which produced
                // duplicate slides on the first run of these checks.
                _ = doc.DocumentNode.OuterHtml;
                var root = doc.DocumentNode;

                var allSlides = root.Descendants()
                    .Where(n => n.NodeType == HtmlNodeType.Element
                                && (n.Name == "div" || n.Name == "section")
                                && HasClass(n, "slide"))
                    .ToList();
                if (allSlides.Count == 0) return html;

                var frame = root.Descendants().FirstOrDefault(n => n.NodeType == HtmlNodeType.Element
                    && n.Name == "div"
                    && (HasClass(n, "frame-16-9") || n.Id == "presentation-frame"));

                if (frame != null && HasId(root, "prev-slide") && HasId(root, "next-slide") && HasDeckNavScript(root))
                    return InjectFixContentSizeScript(html);

                var body = root.SelectSingleNode("//body");
                if (body == null)
                {
                    var htmlEl = root.SelectSingleNode("//html");
                    body = doc.CreateElement("body");
                    if (htmlEl != null)
                    {
                        foreach (var n in htmlEl.ChildNodes.Where(c => c.Name != "head").ToList())
                            body.AppendChild(n);
                        htmlEl.PrependChild(body);
                        if (htmlEl.SelectSingleNode("head") == null)
                            htmlEl.PrependChild(doc.CreateElement("head"));
                    }
                    else
                    {
                        foreach (var n in root.ChildNodes.ToList())
                            body.AppendChild(n);
                        root.AppendChild(body);
                    }
                }

                // Slides nested inside another slide belong to that page and stay where they are.
                var topSlides = allSlides.Where(s => !s.Ancestors().Any(a => HasClass(a, "slide"))).ToList();
                if (topSlides.Count == 0) return html;

                if (frame == null)
                {
                    frame = doc.CreateElement("div");
                    frame.SetAttributeValue("class", "frame-16-9");
                    frame.SetAttributeValue("id", "presentation-frame");
                    // Inserted where the first slide was, so anything before it (an animated
                    // background layer, a hero element) keeps its place in front of the deck.
                    topSlides[0].ParentNode.InsertAfter(frame, topSlides[0]);
                }
                foreach (var s in topSlides) frame.AppendChild(s);   // moves each slide in, document order kept

                for (int i = 0; i < topSlides.Count; i++)
                {
                    if (i == 0) AddClass(topSlides[i], "active");
                    else RemoveClass(topSlides[i], "active");
                }

                // Drop leftover/partial deck navigation, then lay down the canonical one: an earlier
                // pass that half-copied the template leaves buttons wired to nothing, which is worse
                // than the clean controls installed here.
                foreach (var n in root.Descendants().Where(x => x.NodeType == HtmlNodeType.Element
                             && (x.Id == "prev-slide" || x.Id == "next-slide" || x.Id == "slide-counter")).ToList())
                    n.ParentNode?.RemoveChild(n);
                foreach (var s in root.Descendants().Where(x => x.Name == "script" && LooksLikeDeckNavScript(x.InnerText)).ToList())
                    s.ParentNode?.RemoveChild(s);

                var head = root.SelectSingleNode("//head");
                if (head != null) InsertFragmentAtStart(head, DeckScaffoldCss);

                AppendFragment(body, "<button id=\"prev-slide\" class=\"nav-btn\" aria-label=\"Previous slide\">&#8249;</button>"
                    + "<button id=\"next-slide\" class=\"nav-btn\" aria-label=\"Next slide\">&#8250;</button>"
                    + $"<div class=\"counter\" id=\"slide-counter\">1 / {topSlides.Count}</div>"
                    + "<script>" + DeckRuntimeJs + "</script>");

                Log.LogStep($"PresentationTool.EnsureDeckScaffold: rebuilt the slide show — {topSlides.Count} slide(s) moved into #presentation-frame with prev/next, counter and the deck runtime");
                return doc.DocumentNode.OuterHtml;
            }
            catch (Exception ex)
            {
                Log.LogStep($"PresentationTool.EnsureDeckScaffold: left the HTML untouched ({ex.Message})");
                return html;
            }
        }

        private static bool HasClass(HtmlNode node, string cls)
        {
            var attr = node.GetAttributeValue("class", "");
            if (string.IsNullOrWhiteSpace(attr)) return false;
            return attr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                       .Any(c => string.Equals(c, cls, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Parses an HTML fragment and inserts its nodes at the very start of
        /// <paramref name="parent"/>, keeping their order. Used for the scaffold style block, which has
        /// to precede the document's own styles so that whatever the document declares still wins.</summary>
        private static void InsertFragmentAtStart(HtmlNode parent, string htmlFragment)
        {
            var frag = new HtmlDocument();
            frag.LoadHtml(htmlFragment);
            _ = frag.DocumentNode.OuterHtml;   // materialize the fragment before its nodes are moved out
            var nodes = frag.DocumentNode.ChildNodes.ToList();
            for (int i = nodes.Count - 1; i >= 0; i--) parent.PrependChild(nodes[i]);
        }

        /// <summary>Parses an HTML fragment and appends its nodes to <paramref name="parent"/> in order.</summary>
        private static void AppendFragment(HtmlNode parent, string htmlFragment)
        {
            var frag = new HtmlDocument();
            frag.LoadHtml(htmlFragment);
            _ = frag.DocumentNode.OuterHtml;   // materialize the fragment before its nodes are moved out
            foreach (var n in frag.DocumentNode.ChildNodes.ToList())
                parent.AppendChild(n);
        }

        private static void AddClass(HtmlNode node, string cls)
        {
            if (HasClass(node, cls)) return;
            var attr = node.GetAttributeValue("class", "");
            node.SetAttributeValue("class", string.IsNullOrWhiteSpace(attr) ? cls : attr.TrimEnd() + " " + cls);
        }

        private static void RemoveClass(HtmlNode node, string cls)
        {
            var attr = node.GetAttributeValue("class", "");
            if (string.IsNullOrWhiteSpace(attr)) return;
            var kept = attr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                           .Where(c => !string.Equals(c, cls, StringComparison.OrdinalIgnoreCase));
            node.SetAttributeValue("class", string.Join(" ", kept));
        }

        private static bool HasId(HtmlNode root, string id) =>
            root.Descendants().Any(n => n.NodeType == HtmlNodeType.Element && n.Id == id);

        /// <summary>True when a document already carries a working slide-navigation script (the shipped
        /// template's own or the runtime installed here). Matched on the markers that such a script must
        /// contain; an ordinary chart/animation script never mentions the slide counter.</summary>
        private static bool HasDeckNavScript(HtmlNode root) =>
            root.Descendants().Any(n => n.Name == "script" && LooksLikeDeckNavScript(n.InnerText));

        private static bool LooksLikeDeckNavScript(string script) =>
            script.Contains(DeckScaffoldMarker, StringComparison.Ordinal)
            || (script.Contains("slide-counter", StringComparison.OrdinalIgnoreCase)
                && script.Contains(".slide", StringComparison.OrdinalIgnoreCase)
                && script.Contains("keydown", StringComparison.OrdinalIgnoreCase));

        /// <summary>Environment switch for the automatic "show me the deck" delivery: unset = on (the
        /// person who asked for a presentation wants to see it), 0/false/off/no = never open — for batch
        /// runs and automated tests where no window may appear.</summary>
        internal const string DeckAutoOpenEnvVar = "AGENTBRIDGE_DECK_AUTOOPEN";

        /// <summary>Opens the saved deck in the system default browser so the user sees the presentation
        /// instead of having to go looking for the file (issue #106: "the generated file is not opened
        /// after the request is completed"). Runs only when the orchestrator marked a real local desktop
        /// session (<see cref="IsWatchAllowed"/>) and the switch was not turned off; called after the
        /// file is written and versioned, and a failure to launch a browser never fails the operation.</summary>
        private void OpenDeck(string hostPath)
        {
            if (!IsWatchAllowed || !DeckAutoOpen) return;
            try
            {
                Log.LogStep($"PresentationTool.OpenDeck: opening '{hostPath}' in the default browser");
                Process.Start(new ProcessStartInfo(hostPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.LogStep($"PresentationTool.OpenDeck: could not open the browser ({ex.Message}); the deck is saved at '{hostPath}'");
            }
        }

        private static bool DeckAutoOpen
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable(DeckAutoOpenEnvVar)?.Trim();
                if (string.IsNullOrEmpty(raw)) return true;
                return !(raw.Equals("0", StringComparison.Ordinal)
                    || raw.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || raw.Equals("off", StringComparison.OrdinalIgnoreCase)
                    || raw.Equals("no", StringComparison.OrdinalIgnoreCase));
            }
        }


        /// <summary>Injects the animated background block (assets/&lt;style&gt;.bg) before &lt;/head&gt; so the
        /// deck body gets a style-matching animated background. The block is looked up by style name
        /// (case-insensitive); when the current style has no block, a random one is picked. Missing
        /// assets or a missing &lt;/head&gt; leave the HTML unchanged.</summary>
        internal static string InjectAnimatedBackground(string html, string style)
        {
            string[] candidates;
            try { candidates = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "assets"), "*.bg"); }
            catch { return html; }
            if (candidates.Length == 0) return html;
            var file = candidates.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Equals(style, StringComparison.OrdinalIgnoreCase))
                ?? candidates[Random.Shared.Next(candidates.Length)];
            string block;
            try { block = File.ReadAllText(file, Encoding.UTF8); }
            catch { return html; }

            if (html.Contains("</head>", StringComparison.OrdinalIgnoreCase)
                && html.Contains("--color-slide-bg", StringComparison.OrdinalIgnoreCase))
            {
                // Only for a document built on the shipped template, which defines --color-slide-bg as
                // its slide layer: making it 75% opaque lets the animated background show through the
                // slide, which is the intended look. A document that does not define that variable must
                // keep its own opaque slide background, or its text would sit straight on the moving
                // background and become unreadable.
                var addTransparent = "<style>.slide {background-color: color-mix(in srgb, var(--color-slide-bg) 75%, transparent) !important;}</style>";
                html = html.Replace("</head>", addTransparent + "\n</head>", StringComparison.OrdinalIgnoreCase);
            }

            return html.Contains("</head>", StringComparison.OrdinalIgnoreCase)
                ? html.Replace("</head>", block + "\n</head>", StringComparison.OrdinalIgnoreCase)
                : html;
        }

        private static string? MimeFor(string path) =>
            Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".svg" => "image/svg+xml",
                ".webp" => "image/webp",
                _ => null
            };

        private static readonly HashSet<HtmlParseErrorCode> CriticalErrors = new()
        {
            HtmlParseErrorCode.TagNotClosed, HtmlParseErrorCode.TagNotOpened,
            HtmlParseErrorCode.EndTagNotRequired, HtmlParseErrorCode.EndTagInvalidHere
        };

        private static bool IsValidHtml5(string html, out List<string> errors)
        {
            errors = new List<string>();
            if (string.IsNullOrWhiteSpace(html))
            {
                errors.Add("HTML is empty.");
                return false;
            }
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            foreach (var e in doc.ParseErrors)
            {
                if (CriticalErrors.Contains(e.Code))
                    errors.Add($"Line {e.Line}, Pos {e.LinePosition}: {e.Reason}");
            }
            return errors.Count == 0;
        }

        // ---------- JSON ----------

        private static T? TryParseJson<T>(string raw) where T : class
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            try { return JsonSerializer.Deserialize<T>(raw.Substring(start, end - start + 1), JsonOpts); }
            catch (JsonException) { return null; }
        }

        private sealed class SufficiencyOpinion
        {
            public bool Sufficient { get; set; }
            public bool DescriptionClear { get; set; }
            public List<string>? Explanation { get; set; }
        }

        private sealed class ChangeVerdict
        {
            public bool Clear { get; set; }
            public List<string>? Explanation { get; set; }
        }

        // ---------- File helpers ----------

        private static string ReadTextCapped(string path, int maxChars)
        {
            if (!File.Exists(path)) return string.Empty;
            var text = File.ReadAllText(path);
            return text.Length <= maxChars ? text : text[..maxChars] + "\n…[truncated]";
        }

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value[..max] + "…";

        // ---------- Deck template (embedded resource: Assets/template.html) ----------

        private static readonly Lazy<string> TemplateHtmlLazy = new(() =>
        {
            var assembly = typeof(PresentationTool).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".template.html", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Embedded resource 'Assets/template.html' not found in PresentationTool.");
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null)
                throw new InvalidOperationException("Embedded resource 'Assets/template.html' not found in PresentationTool.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Deck template loaded from the embedded resource (Assets/template.html), so the
        /// HTML source of truth lives in a real file and ships inside the plugin assembly.</summary>
        internal static string TemplateHtml => TemplateHtmlLazy.Value;
    }
}
