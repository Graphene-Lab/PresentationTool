using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using HtmlAgilityPack;
using SixLabors.ImageSharp;
using System.Diagnostics;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PresentationPlugin.Harness")]

namespace AIOrchestrator.API
{
    /// <summary>Presentation operations for agent use: create and update a self-contained HTML deck from a description or change request. File paths are Unix-style, relative to the workspace root — never escape it.</summary>
    public class PresentationPlugin : BaseAgentTool, IFileTool
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
        private static readonly string[] Styles = ["Modern", "Vintage", "Minimalist White", "Brutalist", "Retro Pop", "Vaporwave", "Biophilic Design", "Cyberpunk Neon", "Glassmorphism", "Bento Grid", "Retro"];
        private const int MaxHtmlAttempts = 3;
        private const string OnlyOutputAnswer = "- Output only full HTML code. No opening or closing comments, no fences. [Output only]";

        /// <summary>Generate a PowerPoint-style presentation</summary>
        /// <param name="description">What the presentation must cover. The description must include: the subject, a descriptive title and the purpose of the presentation (e.g. "Present the Q3 2026 sales results, titled 'Record Quarter', to the management team — 5 slides"). Keep it a guideline: put the supporting material in contextText or contextFile, otherwise the tool rejects the request for lack of material.</param>
        /// <param name="style">Optional graphic style that shapes the deck (e.g. Modern, Vintage, Minimalist White, Brutalist, Retro Pop, Vaporwave, Biophilic Design, Cyberpunk Neon, Glassmorphism, Bento Grid, Retro.</param>
        /// <param name="contextText">Optional context text the deck content must be based on. (Mandatory if contextFile is missing)</param>
        /// <param name="contextFile">Optional workspace file read as content context (Unix-style path, e.g. "/docs/report.md"). (Mandatory if contextText is missing)</param>
        /// <param name="imageFiles">Optional workspace image files embedded in the deck (Unix-style paths, e.g. "/images/chart.png"). The LLM places each image; each image is used at most once.</param>
        /// <param name="saveFullNameFile">Optional output file path and name (Unix-style, must end with ".html", e.g. "/out/sales.html"). Default: "/presentation/presentation_yyyyMMdd_HHmmss.html" in the workspace.</param>
        /// <returns>The generated .html path in workspace form, or an "Error: ..." message (missing input, unsupported image type, insufficient context, unclear description, LLM failure).</returns>
        public string CreatePresentation(string description, string? style = null, string? contextText = null,
            string? contextFile = null, string[]? imageFiles = null, string? saveFullNameFile = null)
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

            var images = new List<string>();
            if (imageFiles != null)
            {
                foreach (var img in imageFiles.Where(f => !string.IsNullOrWhiteSpace(f)))
                {
                    string imgHost;
                    try { imgHost = SandboxPath.Resolve(img); }
                    catch (UnauthorizedAccessException ex) { return $"Error: {ex.Message}"; }
                    if (!File.Exists(imgHost)) return $"Error: image file '{img}' not found in the workspace.";
                    if (MimeFor(imgHost) == null) return $"Error: unsupported image type for '{img}'. Use png, jpg, gif, bmp, svg or webp.";
                    images.Add(imgHost);
                }
            }

            style ??= Styles[Random.Shared.Next(Styles.Length)];
            Log.LogStep($"PresentationPlugin.CreatePresentation: description='{Truncate(description, 120)}' style={style} images={images!.Count} contextLen={context.Length}");

            var opinion = AskOpinion(description, context.ToString());
            if (opinion == null) return "Error: the LLM returned no usable evaluation of the request. Retry later.";
            if (!opinion.Sufficient || !opinion.DescriptionClear)
                return BuildInsufficientError(opinion);

            var html = GenerateHtml(BuildCreatePrompt(description, style, contextFiles, context.ToString(), images));
            if (html == null) return "Error: the LLM returned no usable HTML after 3 attempts. Retry later.";
            var improved = GenerateHtml(ImproveHtmlCode(html, style));
# if DEBUG
            if (improved == null)
                Debugger.Break();
#endif
            if (improved != null) html = improved;
            else Log.LogStep("PresentationPlugin.CreatePresentation: styling pass failed, keeping first-pass HTML");
            var checkedHtml = GenerateHtml(BuildCheckFixPrompt(html));
            if (checkedHtml != null) html = checkedHtml;
            else Log.LogStep("PresentationPlugin.CreatePresentation: check&fix pass failed, keeping previous HTML");
            html = EmbedImages(html, images);
            html = EmbedSvgIcons(html);
            html = InjectFixContentSizeScript(html);
            html = InjectAnimatedBackground(html, style);

            try
            {
                if (File.Exists(hostPath)) CreateBackup(hostPath);
                File.WriteAllText(hostPath, html);
            }
            catch (Exception ex) { return $"Error: cannot save the presentation. {ex.Message}"; }
            Log.LogStep($"PresentationPlugin.CreatePresentation: wrote '{hostPath}' ({html.Length} chars)");
            return $"Presentation created at {SandboxPath.ToAgent(hostPath)}";
        }

        /// <summary>Updates an existing HTML presentation on request (e.g. change a slide, recolor the deck, add or remove content): the current deck HTML is read, the requested changes are validated for clarity, the LLM produces the updated deck, and the file is overwritten in place. A numbered backup (.NNN.bak) is created before the file is overwritten.</summary>
        /// <param name="filePath">Path of the presentation to update (Unix-style, e.g. "/presentation/sales.html").</param>
        /// <param name="changes">The changes to apply (e.g. "shorten slide 3, change the colors, add a summary slide at the end").</param>
        /// <param name="contextText">Optional extra context the update must respect.</param>
        /// <returns>The updated .html path in workspace form (with the backup name), or an "Error: ..." message (missing input, unclear changes, LLM failure).</returns>
        public string UpdatePresentation(string filePath, string changes, string? contextText = null)
        {
            if (string.IsNullOrWhiteSpace(changes)) return "Error: changes is required.";
            string hostPath;
            try { hostPath = SandboxPath.Resolve(filePath); }
            catch (UnauthorizedAccessException ex) { return $"Error: {ex.Message}"; }
            if (!File.Exists(hostPath)) return $"Error: file '{filePath}' not found in the workspace.";
            if (!hostPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                return "Error: only .html presentations can be updated (they are generated by CreatePresentation).";

            string currentHtml;
            try { currentHtml = File.ReadAllText(hostPath); }
            catch (Exception ex) { return $"Error: cannot read the presentation. {ex.Message}"; }

            Log.LogStep($"PresentationPlugin.UpdatePresentation: '{hostPath}' changes='{Truncate(changes, 120)}' contextText='{Truncate(contextText ?? "", 120)}'");
            var verdict = AskChangesClear(changes, contextText);
            if (verdict == null) return "Error: the LLM returned no usable evaluation of the changes. Retry later.";
            if (!verdict.Clear)
                return $"Error: the requested changes are not clear enough to apply. {Reasons(verdict.Explanation, "the changes do not say what to modify")}";

            var html = GenerateHtml(BuildUpdatePrompt(currentHtml, changes, contextText));
            if (html == null) return "Error: the LLM returned no usable HTML after 3 attempts. Retry later.";
            var checkedHtml = GenerateHtml(BuildCheckFixPrompt(html));
            if (checkedHtml != null) html = checkedHtml;
            else Log.LogStep("PresentationPlugin.UpdatePresentation: check&fix pass failed, keeping updated HTML");
            html = EmbedSvgIcons(html);

            string? backup = null;
            try
            {
                backup = CreateBackup(hostPath);
                File.WriteAllText(hostPath, html);
            }
            catch (Exception ex) { return $"Error: cannot apply the changes. {ex.Message}"; }

            Log.LogStep($"PresentationPlugin.UpdatePresentation: '{hostPath}' updated ({html.Length} chars) backup='{backup ?? "(none)"}'");
            return backup == null
                ? $"Presentation updated at {SandboxPath.ToAgent(hostPath)}"
                : $"Presentation updated at {SandboxPath.ToAgent(hostPath)}. Previous version backed up as '{SandboxPath.ToAgent(Path.Combine(Path.GetDirectoryName(hostPath)!, backup))}'.";
        }

        // ---------- LLM ----------

        /// <summary>Asks the LLM (no history) whether the given context and description are enough to build the deck; returns the JSON verdict or null when the LLM fails.</summary>
        private static SufficiencyOpinion? AskOpinion(string description, string context)
        {
            using var llm = new LLMUtility(Setup.ProviderConfig.ProviderName);
            var prompt = $$"""
                Today's date: {{DateTime.Now:yyyy-MM-dd}}

                You check whether the material provided is sufficient to fulfill the presentation request.
                The description serves as a guideline for the presentation.
                Do NOT proceed when the material cannot support a well-made deck: list exactly what is missing.
                Rules:
                - A well-known topic (e.g. "history of coffee", "solar panel industry") is ALWAYS sufficient: general knowledge builds the deck.
                - Do NOT reject for missing details that general knowledge or design can supply: timelines, statistics, figures, market segments, slide structure, visual assets. Those are NOT missing material.
                - Reject ONLY when the deck cannot be built at all: empty/meaningless description, or a SPECIFIC subject (a company, an event, internal data) whose essential facts are absent from the provided material — then list exactly what is missing.

                Description: {{description}}

                Material (context):
                ```text
                {{(string.IsNullOrWhiteSpace(context) ? "(none provided)" : context)}}
                ```

                Respond with ONLY JSON (no fences, no commentary):
                {"sufficient": true|false, "descriptionClear": true|false, "explanation": ["what is missing or unclear", ...]}
                - "sufficient" = false when the material cannot cover what the request needs.
                - "descriptionClear" = false when the description is ambiguous (unclear subject, title or purpose).
                - "explanation" lists the concrete missing/unclear items when a flag is false; empty otherwise.
                """;
            var (response, hResult) = llm.SendQuery(prompt, useHistory: false, role: LLMUtility.SystemRole.DocumentPreparer,
                forceJsonResponse: true);
            if (hResult != null || string.IsNullOrWhiteSpace(response)) return null;
            var opinion = TryParseJson<SufficiencyOpinion>(response);
            if (opinion == null) Log.LogStep($"PresentationPlugin.AskOpinion: unparseable JSON response");
            else Log.LogStep($"PresentationPlugin.AskOpinion: sufficient={opinion.Sufficient} descriptionClear={opinion.DescriptionClear}");
            return opinion;
        }

        /// <summary>Asks the LLM (no history) whether the requested changes (and optional context) are clear enough to apply; returns the JSON verdict or null when the LLM fails.</summary>
        private static ChangeVerdict? AskChangesClear(string changes, string? contextText)
        {
            using var llm = new LLMUtility(Setup.ProviderConfig.ProviderName);
            var prompt = $$"""
                Today's date: {{DateTime.Now:yyyy-MM-dd}}

                You are about to edit an existing presentation. Validate that the requested changes are clear enough to apply.
                Rules:
                - Decide sensible details yourself (e.g. which slide is "the first", which shade of blue, what "colors" means) — these do NOT make the request unclear.
                - Reject ONLY when the changes are genuinely unusable: empty, meaningless or contradictory requests.
                {{(!string.IsNullOrWhiteSpace(contextText) ? "Additional context: " + contextText : "")}}

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
            if (verdict == null) Log.LogStep($"PresentationPlugin.AskChangesClear: unparseable JSON response");
            else Log.LogStep($"PresentationPlugin.AskChangesClear: clear={verdict.Clear}");
            return verdict;
        }

        /// <summary>Generates deck HTML via the LLM (no history), validates it as HTML5 and retries up to <see cref="MaxHtmlAttempts"/> times, feeding back the validation errors. Returns null when all attempts fail.</summary>
        private static string? GenerateHtml(string prompt)
        {
            using var llm = new LLMUtility(Setup.ProviderConfig.ProviderName);
            for (int attempt = 1; attempt <= MaxHtmlAttempts; attempt++)
            {
                Log.LogStep($"PresentationPlugin.GenerateHtml: attempt {attempt}/{MaxHtmlAttempts}");
                var (response, hResult) = llm.SendQuery(prompt, useHistory: false, role: LLMUtility.SystemRole.DocumentPreparer);
                if (hResult != null)
                {
                    Log.LogStep($"PresentationPlugin.GenerateHtml: LLM error hResult={hResult} — aborting");
                    return null;
                }
                if (string.IsNullOrWhiteSpace(response)) continue;
                var html = response;
                if (!Utility.RemoveFencesEncapsulationAndFixTrim(ref html, false))
                {
                    Log.LogStep($"PresentationPlugin.GenerateHtml: malformed fences on attempt {attempt}");
                    continue;
                }
                if (IsValidHtml5(html, out var errors))
                {
                    Log.LogStep($"PresentationPlugin.GenerateHtml: valid HTML on attempt {attempt}");
                    return html;
                }
                Log.LogStep($"PresentationPlugin.GenerateHtml: invalid HTML on attempt {attempt} ({errors.Count} errors: {string.Join(" | ", errors.Take(6))})");
                if (attempt == MaxHtmlAttempts) break;
                var errorFeedback = string.Join("\n", errors.Take(8).Select(e => $"  - {e}"));
                var missingEnd = !html.TrimEnd().EndsWith("</html>", StringComparison.OrdinalIgnoreCase);
                prompt = $"""
                    The previous HTML5 code you provided was not valid.
                    Here is the code that failed:
                    ```html
                    {html}
                    ```
                    Validation errors:
                    {errorFeedback}
                    {(missingEnd ? "The document does not end with the closing </html> tag." : "")}

                    Please fix ALL the errors above and provide a corrected, valid HTML5 version.
                    {OnlyOutputAnswer}
                    """;
            }
            return null;
        }

        private static string BuildCreatePrompt(string description, string style, List<string> contextFiles, string context, List<string> images)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Today's date: " + DateTime.Now.ToString("yyyy-MM-dd"));
            sb.AppendLine("Create a website of presentation using these context documents.");
            sb.AppendLine("Presentation description: " + description);
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
            sb.AppendLine("- Contrast font colors in both themes.");
            if (images.Count > 0)
            {
                sb.AppendLine("Available images (reference by file name only, e.g. <img src=\"chart.png\">):");
                foreach (var img in images) sb.AppendLine("- " + DescribeImage(img));
            }
            sb.AppendLine("- Use square SVG icons with a self-explanatory file name that can encode size and color: <icon-name>.<size>.<rrggbb>.svg (these files will be auto-generated based on the name you give them). Usage example: disc.32.aa0000.svg (a disc icon, 32x32 px, hex color #aa0000) → <img src=\"disc.32.aa0000.svg\" alt=\"disc\">");
            sb.AppendLine("- Use this template (keep its CSS classes, light and dark theme, navigation buttons and script unchanged; fill the .slide elements with the deck slides):");
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
            // sb.AppendLine("- Preserve the basic slide structure.");
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
            // sb.AppendLine("- Preserve the basic slide structure.");
            sb.AppendLine("HTML code:");
            sb.AppendLine("```html");
            sb.AppendLine(currentHtml);
            sb.AppendLine("```");
            sb.AppendLine(OnlyOutputAnswer);
            return sb.ToString();
        }

        private static string BuildUpdatePrompt(string currentHtml, string changes, string? contextText)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Today's date: " + DateTime.Now.ToString("yyyy-MM-dd"));
            sb.AppendLine("Edit an existing PowerPoint presentation (16:9 deck) rendered as a single HTML file.");
            sb.AppendLine("Apply the requested changes LITERALLY to the HTML below:");
            sb.AppendLine("- The exact strings in the changes request (titles, labels, text) MUST appear verbatim in the output — do not reword or replace them.");
            sb.AppendLine("- Change ONLY what the changes request; keep the rest of the content, wording and structure identical.");
            sb.AppendLine("- Keep the template's CSS classes, navigation buttons and script unchanged; edit the .slide elements (and styles only if needed).");
            sb.AppendLine();
            sb.AppendLine("Current presentation HTML:");
            sb.AppendLine("```html");
            sb.AppendLine(currentHtml);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("Requested changes: " + changes);
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
        /// must never fail the deck creation.</summary>
        internal static string InjectFixContentSizeScript(string html)
        {
            try
            {
                var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "fix-content-size.js"), Encoding.UTF8);
                return html.Replace("</body>", script + "\n</body>", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.LogStep($"PresentationPlugin.InjectFixContentSizeScript: asset not injected: {ex.Message}");
                return html;
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

            if (html.Contains("</head>", StringComparison.OrdinalIgnoreCase))
            {
                // add 75% of transparency
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

        /// <summary>One-line image description for the LLM prompt: file name, descriptive tag, workspace path, pixel size, and (for PNG) whether the background is transparent.</summary>
        private static string DescribeImage(string img)
        {
            var sb = new StringBuilder(Path.GetFileName(img));
            sb.Append(" (");
            var tag = ImageTag(img);
            if (tag.Length > 0) sb.Append("tag: ").Append(tag).Append(", ");
            sb.Append("workspace path ").Append(SandboxPath.ToAgent(img));
            var size = ImageSize(img);
            if (size != null)
                sb.Append(", ").Append(size.Value.Width).Append('x').Append(size.Value.Height).Append(" px");
            if (img.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                sb.Append(PngHasTransparentBackground(img) ? ", transparent background" : ", opaque background");
            sb.Append(')');
            return sb.ToString();
        }

        /// <summary>Human-readable tag derived deterministically from the file name: splits PascalCase/CamelCase words and turns "-"/"_" separators into spaces, capitalizing each word (MilanoDuomo → "Milano Duomo", milano_duomo → "Milano Duomo"). Empty when the name has no letters.</summary>
        private static string ImageTag(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            name = Regex.Replace(name, @"[-_]+", " ");
            name = Regex.Replace(name, @"(?<=[a-z0-9])(?=[A-Z])", " ");
            return string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        }

        /// <summary>Pixel size read deterministically from the file header (ImageSharp Identify; SVG via its width/height/viewBox attributes). Null when the size cannot be determined.</summary>
        private static (int Width, int Height)? ImageSize(string path)
        {
            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                return SvgSize(path);
            try
            {
                using var fs = File.OpenRead(path);
                var info = Image.Identify(fs);
                return (info.Width, info.Height);
            }
            catch { return null; }
        }

        private static (int Width, int Height)? SvgSize(string path)
        {
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                var wm = Regex.Match(text, @"width\s*=\s*[""'](\d+(?:\.\d+)?)");
                var hm = Regex.Match(text, @"height\s*=\s*[""'](\d+(?:\.\d+)?)");
                if (wm.Success && hm.Success)
                    return ((int)double.Parse(wm.Groups[1].Value, CultureInfo.InvariantCulture),
                            (int)double.Parse(hm.Groups[1].Value, CultureInfo.InvariantCulture));
                var vb = Regex.Match(text, @"viewBox\s*=\s*[""']\s*[-\d.]+\s+[-\d.]+\s+(\d+(?:\.\d+)?)\s+(\d+(?:\.\d+)?)");
                return vb.Success
                    ? ((int)double.Parse(vb.Groups[1].Value, CultureInfo.InvariantCulture),
                       (int)double.Parse(vb.Groups[2].Value, CultureInfo.InvariantCulture))
                    : null;
            }
            catch { return null; }
        }

        /// <summary>Deterministic PNG header check: the image has transparency when the IHDR color type carries an alpha channel (4 or 6) or a tRNS chunk is present. No pixel decoding.</summary>
        private static bool PngHasTransparentBackground(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                Span<byte> header = stackalloc byte[26];
                if (fs.Read(header) < 26) return false;
                if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47) return false; // not a PNG
                var colorType = header[25];
                if (colorType is 4 or 6) return true;
                if (colorType is 0 or 2 or 3)
                {
                    long pos = 8;
                    Span<byte> chunkHead = stackalloc byte[8];
                    while (pos + 8 <= fs.Length)
                    {
                        fs.Position = pos;
                        if (fs.Read(chunkHead) < 8) break;
                        var len = (chunkHead[0] << 24) | (chunkHead[1] << 16) | (chunkHead[2] << 8) | chunkHead[3];
                        var type = Encoding.ASCII.GetString(chunkHead[4..8]);
                        if (type == "tRNS") return true;
                        if (type == "IEND") break;
                        pos += 12 + len;
                    }
                }
                return false;
            }
            catch { return false; }
        }

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

        private static string? CreateBackup(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            var dir = Path.GetDirectoryName(filePath) ?? ".";
            var name = Path.GetFileNameWithoutExtension(filePath);
            for (int i = 1; i <= 9999; i++)
            {
                var backup = $"{name}.{i:D3}.bak";
                if (!File.Exists(Path.Combine(dir, backup)))
                {
                    File.Copy(filePath, Path.Combine(dir, backup));
                    return backup;
                }
            }
            var fallback = $"{name}.{DateTime.Now:yyyyMMddHHmmss}.bak";
            File.Copy(filePath, Path.Combine(dir, fallback));
            return fallback;
        }

        private static string ReadTextCapped(string path, int maxChars)
        {
            var text = File.ReadAllText(path);
            return text.Length <= maxChars ? text : text[..maxChars] + "\n…[truncated]";
        }

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value[..max] + "…";

        // ---------- Deck template (embedded resource: Assets/template.html) ----------

        private static readonly Lazy<string> TemplateHtmlLazy = new(() =>
        {
            var assembly = typeof(PresentationPlugin).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".template.html", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Embedded resource 'Assets/template.html' not found in PresentationPlugin.");
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null)
                throw new InvalidOperationException("Embedded resource 'Assets/template.html' not found in PresentationPlugin.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Deck template loaded from the embedded resource (Assets/template.html), so the
        /// HTML source of truth lives in a real file and ships inside the plugin assembly.</summary>
        internal static string TemplateHtml => TemplateHtmlLazy.Value;
    }
}
