using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using Drawing = DocumentFormat.OpenXml.Drawing;
using SixLabors.ImageSharp;

namespace AIOrchestrator.API
{
    /// <summary>
    /// PowerPoint (PPTX) operations for agent use: create a deck from a description, fix an existing deck
    /// from requested changes. File paths are Unix-style, relative to the workspace root — never escape it.
    /// </summary>
    public class PresentationPlugin : BaseAgentTool, IFileTool
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        // 16:9 slide geometry (EMU). Title box + body box of the "Title and Content" layout.
        private const long TitleX = 838200, TitleY = 365125, TitleCx = 10515600, TitleCy = 1325563;
        private const long BodyX = 838200, BodyY = 1825625, BodyCx = 10515600, BodyCy = 4351338;
        private const long ImageX = 7100000, ImageY = 2000000, ImageMax = 4300000;
        private const int SlideCx = 12192000, SlideCy = 6858000;

        /// <summary>Creates a PowerPoint (PPTX) presentation from a description. The LLM designs the deck
        /// outline (title, slides, bullets, image placement) and the tool builds the file. Start here for any new deck.</summary>
        /// <param name="description">What the presentation must cover: topic, audience, tone and length
        /// (e.g. "5 slides presenting the Q3 sales results to the management team").</param>
        /// <param name="style">Optional graphic style of the presentation that shapes the wording
        /// (e.g. "minimalist, dark background, short phrases").</param>
        /// <param name="contextText">Optional context text the deck content must be based on.</param>
        /// <param name="contextFile">Optional workspace file read as content context (Unix-style path, e.g. "/docs/report.md").</param>
        /// <param name="imageFiles">Optional workspace image files to embed on slides (Unix-style paths, e.g. "/images/chart.png").
        /// The LLM decides which slide shows each image; each image is used at most once.</param>
        /// <param name="savePath">Optional output file path and name (Unix-style, must end with ".pptx",
        /// e.g. "/out/sales.pptx"). Default: "/presentation_yyyyMMdd_HHmmss.pptx" in the workspace root.</param>
        /// <returns>The generated .pptx path in workspace form, or an "Error: ..." message
        /// (missing input, unsupported file type, LLM failure).</returns>
        public string CreatePresentation(string description, string? style = null, string? contextText = null,
            string? contextFile = null, string[]? imageFiles = null, string? savePath = null)
        {
            if (string.IsNullOrWhiteSpace(description))
                return "Error: description is required.";
            if (savePath != null && !savePath.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
                return "Error: savePath must end with '.pptx'.";

            string hostPath;
            try
            {
                hostPath = SandboxPath.Resolve(savePath
                    ?? $"/presentation_{DateTime.Now:yyyyMMdd_HHmmss}.pptx");
            }
            catch (UnauthorizedAccessException ex) { return $"Error: {ex.Message}"; }
            Directory.CreateDirectory(Path.GetDirectoryName(hostPath)!);

            var context = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(contextText)) context.AppendLine(contextText);
            if (contextFile != null)
            {
                string ctxHost;
                try { ctxHost = SandboxPath.Resolve(contextFile); }
                catch (UnauthorizedAccessException ex) { return $"Error: {ex.Message}"; }
                if (!File.Exists(ctxHost)) return $"Error: context file '{contextFile}' not found in the workspace.";
                context.AppendLine(ReadTextCapped(ctxHost, 60_000));
            }

            var images = new List<string>();
            if (imageFiles != null)
            {
                foreach (var img in imageFiles)
                {
                    string imgHost;
                    try { imgHost = SandboxPath.Resolve(img); }
                    catch (UnauthorizedAccessException ex) { return $"Error: {ex.Message}"; }
                    if (!File.Exists(imgHost)) return $"Error: image file '{img}' not found in the workspace.";
                    try { GetImagePartType(imgHost); }
                    catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
                    images.Add(imgHost);
                }
            }

            Log.LogStep($"PresentationPlugin.CreatePresentation: description='{Truncate(description, 120)}' images={images.Count} contextLen={context.Length}");
            var deck = AskDeck(BuildCreatePrompt(description, style, context.ToString(), images), isFix: false);
            if (deck == null) return "Error: the LLM returned no usable deck outline. Retry later.";
            if (deck.Slides.Count == 0) return "Error: the LLM produced an empty deck outline. Retry.";

            try { BuildDeck(hostPath, deck, images); }
            catch (Exception ex) { return $"Error: cannot build the presentation. {ex.Message}"; }

            Log.LogStep($"PresentationPlugin.CreatePresentation: wrote '{hostPath}' ({deck.Slides.Count + 1} slides)");
            return $"Presentation created at {SandboxPath.ToAgent(hostPath)}";
        }

        /// <summary>Fixes an existing PPTX presentation by applying the requested changes: the current deck
        /// outline is read, the LLM produces the corrected outline, and the deck is updated in place —
        /// existing slides keep their layout and images, new slides are appended, removed ones are deleted.
        /// A numbered backup (.NNN.bak) is created before the file is overwritten.</summary>
        /// <param name="filePath">Path of the presentation to fix (Unix-style, e.g. "/out/sales.pptx").</param>
        /// <param name="changes">The changes to apply (e.g. "shorten slide 3, add a summary slide at the end,
        /// fix the typo in slide 1").</param>
        /// <param name="contextText">Optional extra context the corrections must respect.</param>
        /// <returns>The fixed .pptx path in workspace form, or an "Error: ..." message.</returns>
        public string FixPresentation(string filePath, string changes, string? contextText = null)
        {
            if (string.IsNullOrWhiteSpace(changes)) return "Error: changes is required.";
            string hostPath;
            try { hostPath = SandboxPath.Resolve(filePath); }
            catch (UnauthorizedAccessException ex) { return $"Error: {ex.Message}"; }
            if (!File.Exists(hostPath)) return $"Error: file '{filePath}' not found in the workspace.";
            if (!hostPath.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
                return "Error: only .pptx files can be fixed.";

            string outline;
            try { outline = ExtractOutline(hostPath); }
            catch (Exception ex) { return $"Error: cannot read the presentation. {ex.Message}"; }

            Log.LogStep($"PresentationPlugin.FixPresentation: '{hostPath}' changes='{Truncate(changes, 120)}'");
            var deck = AskDeck(BuildFixPrompt(outline, changes, contextText), isFix: true);
            if (deck == null) return "Error: the LLM returned no usable deck outline. Retry later.";
            if (deck.Slides.Count == 0) return "Error: the LLM produced an empty deck outline. Retry.";

            try
            {
                CreateBackup(hostPath);
                ApplyDeckInPlace(hostPath, deck);
            }
            catch (Exception ex) { return $"Error: cannot apply the changes. {ex.Message}"; }

            Log.LogStep($"PresentationPlugin.FixPresentation: '{hostPath}' updated to {deck.Slides.Count} slides");
            return $"Presentation fixed at {SandboxPath.ToAgent(hostPath)}";
        }

        // ---------- LLM ----------

        /// <summary>Returns the corrected <see cref="DeckOutline"/> or an "Error: ..." string.</summary>
        private static DeckOutline? AskDeck(string prompt, bool isFix)
        {
            using var llm = new LLMUtility(Setup.ProviderConfig.ProviderName);
            var (response, _) = llm.SendQuery(prompt, maxToken: 8000, temperature: 0.3, forceJsonResponse: true);
            if (string.IsNullOrWhiteSpace(response)) return null;
            var deck = ParseDeckJson(response);
            if (deck == null) return null;
            foreach (var s in deck.Slides)
            {
                if (isFix) s.Image = null;
                else if (s.Image is int idx && idx < 0) s.Image = null;
            }
            return deck;
        }

        private static string BuildCreatePrompt(string description, string? style, string context, List<string> images)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a presentation designer. Design a PowerPoint deck from the request below.");
            sb.AppendLine("Respond with ONLY a JSON object (no markdown fences, no commentary) matching this exact schema:");
            sb.AppendLine("{\"title\": string, \"slides\": [{\"title\": string, \"bullets\": [string], \"image\": int|null}]}");
            sb.AppendLine("Rules:");
            sb.AppendLine("- 3 to 12 slides. The first slide is the title slide; the rest are content slides.");
            sb.AppendLine("- Each bullet is a short phrase (max ~12 words); 2 to 6 bullets per slide.");
            sb.AppendLine("- \"image\" is the 0-based index into the provided image list for the slide picture, or null; at most one image per slide, each image used at most once.");
            sb.AppendLine("- Content must follow the description, the style and any provided context. Write the content in the language of the description.");
            sb.AppendLine();
            sb.AppendLine("Description: " + description);
            if (!string.IsNullOrWhiteSpace(style)) sb.AppendLine("Graphic style: " + style);
            if (!string.IsNullOrWhiteSpace(context)) sb.AppendLine("Context:" + Environment.NewLine + context.TrimEnd());
            if (images.Count > 0)
            {
                sb.AppendLine("Available images (index: file name):");
                for (int i = 0; i < images.Count; i++) sb.AppendLine($"  {i}: {Path.GetFileName(images[i])}");
            }
            return sb.ToString();
        }

        private static string BuildFixPrompt(string outline, string changes, string? contextText)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a presentation editor. Below is the current outline of an existing PowerPoint deck.");
            sb.AppendLine("Apply the requested changes and respond with the FULL corrected deck as ONLY a JSON object (no markdown fences, no commentary):");
            sb.AppendLine("{\"title\": string, \"slides\": [{\"title\": string, \"bullets\": [string], \"image\": null}]}");
            sb.AppendLine("Rules:");
            sb.AppendLine("- Keep the slide order and count unless the changes require otherwise.");
            sb.AppendLine("- Keep titles and bullets concise; 2 to 6 bullets per slide.");
            sb.AppendLine("- \"image\" must always be null.");
            sb.AppendLine("- The deck title stays the same unless the changes say otherwise. Write in the language of the current outline.");
            sb.AppendLine();
            sb.AppendLine("Current deck outline:");
            sb.AppendLine(outline.TrimEnd());
            sb.AppendLine();
            sb.AppendLine("Requested changes: " + changes);
            if (!string.IsNullOrWhiteSpace(contextText)) sb.AppendLine("Additional context:" + Environment.NewLine + contextText);
            return sb.ToString();
        }

        private static DeckOutline? ParseDeckJson(string raw)
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            var json = raw.Substring(start, end - start + 1);
            try { return JsonSerializer.Deserialize<DeckOutline>(json, JsonOpts); }
            catch (JsonException) { return null; }
        }

        // ---------- PPTX builder (OOXML, transitional) ----------

        private static void BuildDeck(string hostPath, DeckOutline deck, List<string> images)
        {
            using var doc = PresentationDocument.Create(hostPath, PresentationDocumentType.Presentation);
            var presPart = doc.AddPresentationPart()!;

            // Theme under the presentation part, shared to the master (PowerPoint requirement).
            var themePart = presPart.AddNewPart<ThemePart>("rId2")!;
            var masterPart = presPart.AddNewPart<SlideMasterPart>("rId1")!;
            masterPart.AddPart(themePart);
            themePart.Theme = BuildTheme();
            themePart.Theme.Save();

            var titleLayoutPart = masterPart.AddNewPart<SlideLayoutPart>("rId1")!;
            titleLayoutPart.SlideLayout = BuildTitleLayout();
            titleLayoutPart.SlideLayout.Save();
            titleLayoutPart.AddPart(masterPart);

            var contentLayoutPart = masterPart.AddNewPart<SlideLayoutPart>("rId2")!;
            contentLayoutPart.SlideLayout = BuildContentLayout();
            contentLayoutPart.SlideLayout.Save();
            contentLayoutPart.AddPart(masterPart);

            masterPart.SlideMaster = BuildMaster();
            masterPart.SlideMaster.Save();

            presPart.Presentation = new Presentation(
                new SlideMasterIdList(new SlideMasterId { Id = 2147483648U, RelationshipId = "rId1" }),
                new SlideIdList(),
                new SlideSize { Cx = SlideCx, Cy = SlideCy },
                new NotesSize { Cx = 6858000, Cy = 9144000 });
            presPart.Presentation.Save();

            var slideList = new List<(DeckSlide Slide, bool IsTitle)> { (new DeckSlide { Title = deck.Title }, true) };
            slideList.AddRange(deck.Slides.Select(s => (s, false)));

            uint id = 256;
            for (int i = 0; i < slideList.Count; i++)
            {
                var (slideData, isTitle) = slideList[i];
                var relId = $"rId{3 + i}";
                var slidePart = presPart.AddNewPart<SlidePart>(relId)!;
                slidePart.AddPart(isTitle ? titleLayoutPart : contentLayoutPart);
                slidePart.Slide = isTitle ? BuildTitleSlide(slideData.Title) : BuildContentSlide(slideData, images, slidePart);
                slidePart.Slide.Save();
                presPart.Presentation.SlideIdList!.Append(new SlideId { Id = id++, RelationshipId = relId });
            }
            presPart.Presentation.Save();
        }

        private static Drawing.Theme BuildTheme() => new(
            new Drawing.ThemeElements(
                new Drawing.ColorScheme(
                    new Drawing.Dark1Color(new Drawing.SystemColor { Val = Drawing.SystemColorValues.WindowText, LastColor = "000000" }),
                    new Drawing.Light1Color(new Drawing.SystemColor { Val = Drawing.SystemColorValues.Window, LastColor = "FFFFFF" }),
                    new Drawing.Dark2Color(new Drawing.RgbColorModelHex { Val = "44546A" }),
                    new Drawing.Light2Color(new Drawing.RgbColorModelHex { Val = "E7E6E6" }),
                    new Drawing.Accent1Color(new Drawing.RgbColorModelHex { Val = "4472C4" }),
                    new Drawing.Accent2Color(new Drawing.RgbColorModelHex { Val = "ED7D31" }),
                    new Drawing.Accent3Color(new Drawing.RgbColorModelHex { Val = "A5A5A5" }),
                    new Drawing.Accent4Color(new Drawing.RgbColorModelHex { Val = "FFC000" }),
                    new Drawing.Accent5Color(new Drawing.RgbColorModelHex { Val = "5B9BD5" }),
                    new Drawing.Accent6Color(new Drawing.RgbColorModelHex { Val = "70AD47" }),
                    new Drawing.Hyperlink(new Drawing.RgbColorModelHex { Val = "0563C1" }),
                    new Drawing.FollowedHyperlinkColor(new Drawing.RgbColorModelHex { Val = "954F72" })
                ) { Name = "Office" },
                new Drawing.FontScheme(
                    new Drawing.MajorFont(
                        new Drawing.LatinFont { Typeface = "Calibri Light" },
                        new Drawing.EastAsianFont { Typeface = "" },
                        new Drawing.ComplexScriptFont { Typeface = "" }),
                    new Drawing.MinorFont(
                        new Drawing.LatinFont { Typeface = "Calibri" },
                        new Drawing.EastAsianFont { Typeface = "" },
                        new Drawing.ComplexScriptFont { Typeface = "" })
                ) { Name = "Office" },
                new Drawing.FormatScheme(
                    new Drawing.FillStyleList(
                        new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor }),
                        new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor }),
                        new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor })),
                    new Drawing.LineStyleList(
                        new Drawing.Outline(new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor })) { Width = 6350, CapType = Drawing.LineCapValues.Flat },
                        new Drawing.Outline(new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor })) { Width = 12700, CapType = Drawing.LineCapValues.Flat },
                        new Drawing.Outline(new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor })) { Width = 19050, CapType = Drawing.LineCapValues.Flat }),
                    new Drawing.EffectStyleList(
                        new Drawing.EffectStyle(new Drawing.EffectList()),
                        new Drawing.EffectStyle(new Drawing.EffectList()),
                        new Drawing.EffectStyle(new Drawing.EffectList())),
                    new Drawing.BackgroundFillStyleList(
                        new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor }),
                        new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor }),
                        new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor }))
                ) { Name = "Office" }
            )) { Name = "Office Theme" };

        private static SlideMaster BuildMaster() => new(
            new CommonSlideData(EmptyShapeTree()),
            new ColorMap
            {
                Background1 = Drawing.ColorSchemeIndexValues.Light1,
                Text1 = Drawing.ColorSchemeIndexValues.Dark1,
                Background2 = Drawing.ColorSchemeIndexValues.Light2,
                Text2 = Drawing.ColorSchemeIndexValues.Dark2,
                Accent1 = Drawing.ColorSchemeIndexValues.Accent1,
                Accent2 = Drawing.ColorSchemeIndexValues.Accent2,
                Accent3 = Drawing.ColorSchemeIndexValues.Accent3,
                Accent4 = Drawing.ColorSchemeIndexValues.Accent4,
                Accent5 = Drawing.ColorSchemeIndexValues.Accent5,
                Accent6 = Drawing.ColorSchemeIndexValues.Accent6,
                Hyperlink = Drawing.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = Drawing.ColorSchemeIndexValues.FollowedHyperlink
            },
            new SlideLayoutIdList(
                new SlideLayoutId { Id = 2147483649U, RelationshipId = "rId1" },
                new SlideLayoutId { Id = 2147483650U, RelationshipId = "rId2" }));

        private static SlideLayout BuildTitleLayout() => new(
            new CommonSlideData(
                new ShapeTree(
                    new GroupShapeProperties(),
                    LayoutPlaceholder(2, "Title", PlaceholderValues.CenteredTitle, 685800, 2130425, 7772400, 1470025),
                    LayoutPlaceholder(3, "Subtitle", PlaceholderValues.SubTitle, 1371600, 3886200, 6400800, 1752600, 1)))
            { Name = "Title Slide" },
            new ColorMapOverride(new Drawing.MasterColorMapping()))
        { Type = SlideLayoutValues.Title };

        private static SlideLayout BuildContentLayout() => new(
            new CommonSlideData(
                new ShapeTree(
                    new GroupShapeProperties(),
                    LayoutPlaceholder(2, "Title", PlaceholderValues.Title, TitleX, TitleY, TitleCx, TitleCy),
                    LayoutPlaceholder(3, "Content", PlaceholderValues.Body, BodyX, BodyY, BodyCx, BodyCy, 1)))
            { Name = "Title and Content" },
            new ColorMapOverride(new Drawing.MasterColorMapping()))
        { Type = SlideLayoutValues.ObjectText };

        private static Slide BuildTitleSlide(string title)
        {
            var tree = ShapeTreeWith(TextShape(2, "Title", title, null, titlePh: true, 685800, 2130425, 7772400, 1470025));
            return new Slide(new CommonSlideData(tree), new ColorMapOverride(new Drawing.MasterColorMapping()));
        }

        private static Slide BuildContentSlide(DeckSlide slide, List<string>? images, SlidePart slidePart)
        {
            var hasImage = slide.Image is int idx && idx >= 0 && images != null && idx < images.Count;
            var bodyCx = hasImage ? 5800000 : BodyCx;
            var tree = ShapeTreeWith(TextShape(2, "Title", slide.Title, null, titlePh: true, TitleX, TitleY, TitleCx, TitleCy));
            tree.Append(TextShape(3, "Body", null, slide.Bullets, titlePh: false, BodyX, BodyY, bodyCx, BodyCy));
            if (hasImage) AddPicture(slidePart, tree, images![slide.Image!.Value]);
            return new Slide(new CommonSlideData(tree), new ColorMapOverride(new Drawing.MasterColorMapping()));
        }

        private static ShapeTree EmptyShapeTree() => ShapeTreeWith();

        private static ShapeTree ShapeTreeWith(params OpenXmlElement[] children)
        {
            var elements = new List<OpenXmlElement>
            {
                new NonVisualGroupShapeProperties(
                    new NonVisualDrawingProperties { Id = 1, Name = "" },
                    new NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties()
            };
            elements.AddRange(children);
            return new ShapeTree(elements);
        }

        private static Shape LayoutPlaceholder(uint id, string name, PlaceholderValues type, long x, long y, long cx, long cy, uint? idx = null)
        {
            var ph = new PlaceholderShape { Type = type };
            if (idx.HasValue) ph.Index = idx.Value;
            return new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties { Id = id, Name = name },
                    new NonVisualShapeDrawingProperties(new Drawing.ShapeLocks { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties(ph)),
                new ShapeProperties(
                    new Drawing.Transform2D(new Drawing.Offset { X = x, Y = y }, new Drawing.Extents { Cx = cx, Cy = cy })),
                new TextBody(new Drawing.BodyProperties(), new Drawing.ListStyle(), new Drawing.Paragraph(new Drawing.EndParagraphRunProperties())));
        }

        private static Shape TextShape(uint id, string name, string? title, IReadOnlyList<string>? bullets, bool titlePh, long x, long y, long cx, long cy)
        {
            var paragraphs = new List<Drawing.Paragraph>();
            if (title != null) paragraphs.Add(TitleParagraph(title));
            if (bullets != null) foreach (var b in bullets) paragraphs.Add(BulletParagraph(b));
            if (paragraphs.Count == 0) paragraphs.Add(new Drawing.Paragraph(new Drawing.EndParagraphRunProperties()));
            var textBody = new TextBody(new Drawing.BodyProperties(), new Drawing.ListStyle());
            foreach (var p in paragraphs) textBody.Append(p);
            var ph = new PlaceholderShape { Type = titlePh ? PlaceholderValues.Title : PlaceholderValues.Body };
            if (!titlePh) ph.Index = 1;
            return new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties { Id = id, Name = name },
                    new NonVisualShapeDrawingProperties(new Drawing.ShapeLocks { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties(ph)),
                new ShapeProperties(
                    new Drawing.Transform2D(new Drawing.Offset { X = x, Y = y }, new Drawing.Extents { Cx = cx, Cy = cy }),
                    new Drawing.PresetGeometry(new Drawing.AdjustValueList()) { Preset = Drawing.ShapeTypeValues.Rectangle }),
                textBody);
        }

        private static Drawing.Paragraph TitleParagraph(string text) =>
            new(new Drawing.ParagraphProperties(), Run(text, 4000, bold: true, accent: true));

        private static Drawing.Paragraph BulletParagraph(string text) =>
            new(new Drawing.ParagraphProperties(
                    new Drawing.BulletFont { Typeface = "Arial" },
                    new Drawing.CharacterBullet { Char = "•" }),
                Run(text, 1800, bold: false, accent: false));

        private static Drawing.Run Run(string text, int size, bool bold, bool accent)
        {
            var rPr = new Drawing.RunProperties { Language = "en-US", FontSize = size, Bold = bold };
            if (accent) rPr.Append(new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.Accent1 }));
            return new Drawing.Run(rPr, new Drawing.Text(text));
        }

        private static void AddPicture(SlidePart slidePart, ShapeTree tree, string imagePath)
        {
            var imagePart = slidePart.AddImagePart(GetImagePartType(imagePath));
            using (var fs = File.OpenRead(imagePath)) imagePart.FeedData(fs);
            var relId = slidePart.GetIdOfPart(imagePart);

            double ar;
            using (var fs = File.OpenRead(imagePath))
            {
                var info = Image.Identify(fs);
                ar = (double)info.Width / info.Height;
            }
            long w = ImageMax, h = (long)(ImageMax / ar);
            if (h > ImageMax) { h = ImageMax; w = (long)(ImageMax * ar); }

            tree.Append(new Picture(
                new NonVisualPictureProperties(
                    new NonVisualDrawingProperties { Id = 5, Name = Path.GetFileName(imagePath) },
                    new NonVisualPictureDrawingProperties(new Drawing.PictureLocks { NoChangeAspect = true }),
                    new ApplicationNonVisualDrawingProperties()),
                new Drawing.BlipFill(
                    new Drawing.Blip { Embed = relId, CompressionState = Drawing.BlipCompressionValues.Print },
                    new Drawing.Stretch(new Drawing.FillRectangle())),
                new ShapeProperties(
                    new Drawing.Transform2D(new Drawing.Offset { X = ImageX, Y = ImageY }, new Drawing.Extents { Cx = w, Cy = h }),
                    new Drawing.PresetGeometry(new Drawing.AdjustValueList()) { Preset = Drawing.ShapeTypeValues.Rectangle })));
        }

        private static PartTypeInfo GetImagePartType(string path) =>
            Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => ImagePartType.Png,
                ".jpg" or ".jpeg" => ImagePartType.Jpeg,
                ".gif" => ImagePartType.Gif,
                ".bmp" => ImagePartType.Bmp,
                _ => throw new InvalidOperationException($"Unsupported image type for '{path}'. Use png, jpg, gif or bmp.")
            };

        // ---------- Outline extraction + in-place fix ----------

        private static string ExtractOutline(string hostPath)
        {
            using var doc = PresentationDocument.Open(hostPath, false);
            var slides = doc.PresentationPart!.SlideParts.ToList();
            var sb = new StringBuilder();
            for (int i = 0; i < slides.Count; i++)
            {
                sb.Append($"Slide {i + 1}: ");
                sb.AppendLine(GetTitleText(slides[i]) ?? "(no title)");
                foreach (var b in GetBulletTexts(slides[i]))
                    sb.AppendLine("  - " + b);
            }
            return sb.ToString();
        }

        private static string? GetTitleText(SlidePart slidePart)
        {
            foreach (var shape in slidePart.Slide?.Descendants<Shape>() ?? Enumerable.Empty<Shape>())
                if (IsTitleType(PlaceholderType(shape)))
                    return GetShapeText(shape);
            return null;
        }

        private static List<string> GetBulletTexts(SlidePart slidePart)
        {
            var result = new List<string>();
            foreach (var shape in slidePart.Slide?.Descendants<Shape>() ?? Enumerable.Empty<Shape>())
                if (PlaceholderType(shape) == PlaceholderValues.Body)
                    foreach (var line in GetShapeText(shape).Split('\n'))
                        if (!string.IsNullOrWhiteSpace(line)) result.Add(line.Trim());
            return result;
        }

        private static string GetShapeText(Shape shape)
        {
            var body = shape.TextBody;
            if (body == null) return "";
            return string.Join("\n", body.Elements<Drawing.Paragraph>()
                .Select(p => string.Concat(p.Elements<Drawing.Run>().Select(r => r.Text?.Text ?? "")))
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        private static PlaceholderValues? PlaceholderType(Shape shape) =>
            shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties
                ?.GetFirstChild<PlaceholderShape>()?.Type?.Value;

        private static bool IsTitleType(PlaceholderValues? type) =>
            type == PlaceholderValues.Title || type == PlaceholderValues.CenteredTitle;

        private static void ApplyDeckInPlace(string hostPath, DeckOutline deck)
        {
            using var doc = PresentationDocument.Open(hostPath, true);
            var presPart = doc.PresentationPart!;
            var slideParts = presPart.SlideParts.ToList();
            var common = Math.Min(slideParts.Count, deck.Slides.Count);
            for (int i = 0; i < common; i++) SetSlideText(slideParts[i], deck.Slides[i]);

            var contentLayout = FindContentLayout(presPart);
            for (int i = common; i < deck.Slides.Count; i++) AppendSlide(presPart, contentLayout, deck.Slides[i]);
            for (int i = slideParts.Count - 1; i >= deck.Slides.Count; i--) RemoveSlide(presPart, slideParts[i]);

            presPart.Presentation!.Save();
        }

        private static void SetSlideText(SlidePart slidePart, DeckSlide slide)
        {
            foreach (var shape in slidePart.Slide?.Descendants<Shape>() ?? Enumerable.Empty<Shape>())
            {
                var type = PlaceholderType(shape);
                if (IsTitleType(type)) SetShapeText(shape, new[] { slide.Title });
                else if (type == PlaceholderValues.Body) SetShapeText(shape, slide.Bullets);
            }
        }

        private static void SetShapeText(Shape shape, IReadOnlyList<string> lines)
        {
            var body = shape.TextBody;
            if (body == null) return;
            body.RemoveAllChildren<Drawing.Paragraph>();
            foreach (var line in lines) body.Append(BulletParagraph(line));
            if (lines.Count == 0) body.Append(new Drawing.Paragraph(new Drawing.EndParagraphRunProperties()));
        }

        private static SlideLayoutPart? FindContentLayout(PresentationPart presPart)
        {
            foreach (var master in presPart.SlideMasterParts)
                foreach (var layout in master.SlideLayoutParts)
                    if (layout.SlideLayout?.CommonSlideData?.Name?.Value is string n && n.Contains("Title and Content"))
                        return layout;
            return presPart.SlideMasterParts.FirstOrDefault()?.SlideLayoutParts.FirstOrDefault();
        }

        private static void AppendSlide(PresentationPart presPart, SlideLayoutPart? layout, DeckSlide slide)
        {
            var slidePart = presPart.AddNewPart<SlidePart>()!;
            if (layout != null) slidePart.AddPart(layout);
            slidePart.Slide = BuildContentSlide(slide, null, slidePart);
            slidePart.Slide.Save();
            var maxId = presPart.Presentation!.SlideIdList!.Elements<SlideId>().Select(x => x.Id!.Value).DefaultIfEmpty(255U).Max();
            presPart.Presentation.SlideIdList.Append(new SlideId { Id = maxId + 1, RelationshipId = presPart.GetIdOfPart(slidePart) });
            presPart.Presentation.Save();
        }

        private static void RemoveSlide(PresentationPart presPart, SlidePart slidePart)
        {
            var relId = presPart.GetIdOfPart(slidePart);
            presPart.Presentation!.SlideIdList!.Elements<SlideId>()
                .FirstOrDefault(x => x.RelationshipId!.Value == relId)?.Remove();
            presPart.DeletePart(slidePart);
            presPart.Presentation.Save();
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

        // ---------- Deck outline JSON model ----------

        private sealed class DeckOutline
        {
            public string Title { get; set; } = "";
            public List<DeckSlide> Slides { get; set; } = new();
        }

        private sealed class DeckSlide
        {
            public string Title { get; set; } = "";
            public List<string> Bullets { get; set; } = new();
            public int? Image { get; set; }
        }
    }
}
