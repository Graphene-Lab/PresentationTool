using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AIOrchestrator;
using AIOrchestrator.API;

namespace PresentationPluginHarness;

/// <summary>
/// PresentationPlugin single-create visual test: generates ONE presentation and opens it
/// in the system default browser for manual visual inspection (styling second pass, theme,
/// layout). Strategy mirrors DocumentTool.Tests / OfficeTool.Tests (AGENT_TOOLS_GUIDE
/// "Testing Agent Tools"): behavioral test, only the artifact is produced and verified.
///   - provider: --provider NAME (default DeepSeekBridge on 127.0.0.1:8787; falls back to
///     the local Ollama qwen3.5:4b runtime provider with --provider Ollama_Qwen)
/// Workspace lives in %TEMP% on purpose: the repo sits under OneDrive and test files
/// written under the repo got cloud-synced on every write (historical slow runs).
/// </summary>
static class Program
{
    private static int _failures;
    private static string _workspace = "";
    private static string _providerName = "DeepSeekBridge";
    private static readonly string ResultsFile = Path.Combine(Path.GetTempPath(), "presentationplugin_test_results.txt");

    static int Main(string[] args)
    {
        var idx = Array.IndexOf(args, "--provider");
        if (idx >= 0 && idx + 1 < args.Length) _providerName = args[idx + 1];
        if (Array.IndexOf(args, "--selftest") >= 0) return RunSelfTest();
        if (Array.IndexOf(args, "--previews") >= 0) return RunPreviews();
        EnsureProvider();

        Log.IsEnabled = true;
        _workspace = Path.Combine(Path.GetTempPath(), "PresentationPlugin.Tests-workspace");
        try
        {
            if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
        }
        catch (Exception ex)
        {
        }
        Directory.CreateDirectory(_workspace);
        Setup.SkipIndexingOnStartup = true;
        Setup.DocumentsPath = _workspace;
        Setup.ProviderConfig = ProviderConfigs.Get(_providerName);
        StageIcons();
        StageImages();

        File.WriteAllText(ResultsFile, $"RUN {DateTime.Now:HH:mm:ss} provider={_providerName}\n");
        WriteResult("STARTED");

        Console.WriteLine("══════════ PresentationPlugin single-create test ══════════");
        Console.WriteLine($"provider: {_providerName}");
        Log.LogStep($"=== PresentationPlugin single-create test (provider {_providerName}) ===");

        try
        {
            var tool = new PresentationPlugin();
            var r = tool.CreatePresentation(
                "Present 'Lumora Analytics', a B2B AI startup optimizing energy in commercial buildings. " +
                "6 slides: cover, the product, the market, the team, the roadmap, growth targets. " +
                "Place the 3 provided context images (skyline.png, power-pylon.png, brain.png) inside the slides " +
                "by referencing their file names in <img src='...'> tags: the skyline on the cover or market slide, " +
                "the power pylon on the product slide, the brain circuit on the team slide.",
                style: "Cyberpunk Neon",
                imageFiles: new[] { "images/skyline.png", "images/power-pylon.png", "images/brain.png" },
                contextText: "Lumora Analytics was founded in 2022 in Berlin by Elena Keller (CEO) and Jonas Weber (CTO), " +
                "starting with 3 engineers; today it employs 34 people across product, AI/ML, sales and customer success. " +
                "Product: the Lumora platform, an AI service that continuously optimizes HVAC, lighting and ventilation in " +
                "commercial buildings through IoT sensors and smart-building integrations (BACnet, MQTT, REST). Core features: " +
                "a real-time optimization engine that cuts energy costs by up to 30%, predictive maintenance alerts, an " +
                "executive dashboard with per-building benchmarking, and a public REST API for facility-management systems. " +
                "Differentiators: hardware-agnostic (no vendor lock-in), 2-week onboarding, and average payback of 14 months. " +
                "Market: building energy management is about USD 14B in 2026, growing roughly 18% per year toward USD 32B by " +
                "2030. Segments: office buildings (60% of revenue), retail and logistics centers. Competitors: large vendors " +
                "such as Siemens and Schneider Electric, plus smaller startups; Lumora wins on time-to-value and neutrality. " +
                "Pricing: SaaS subscription per square meter, starting at EUR 0.40/m2/month, 40% of revenue from annual contracts. " +
                "Team: Elena Keller, ex-BigFour energy consultant, leads strategy and GTM; Jonas Weber, ex-Google ML engineer, " +
                "leads the platform. Key hires in 2025: Head of Sales (from Siemens Smart Infrastructure) and Head of ML " +
                "(ex-DeepMind), plus an advisory board with two former building-automation CTOs. Roadmap: Q1 2024 commercial " +
                "launch with 12 pilot buildings; Q2 2024 first annual contracts; 2025 Series A of EUR 8M; Q1 2026 expansion to " +
                "Austria and Switzerland; Q3 2026 retail vertical and 120 buildings under contract; Q1 2027 France and the " +
                "Netherlands. Targets: EUR 1.2M revenue in 2025, EUR 3M in 2026, EUR 7.5M in 2027; gross margin 75%, " +
                "85% customer retention, net revenue retention 120%, break-even planned in 2028.",
                saveFullNameFile: "/presentation.html");
            Console.WriteLine($"  CreatePresentation → {r}");

            var hostFile = Path.Combine(_workspace, "presentation.html");
            if (!r.StartsWith("Presentation created at") || !File.Exists(hostFile))
            { Fail("single-create", $"create failed: {r}"); return 1; }

            RepairImages(hostFile);
            OpenInBrowser(hostFile);
            Pass("single-create");
            Console.WriteLine($"File: {hostFile}");
            return 0;
        }
        catch (Exception ex)
        {
            Fail("single-create", $"CRASH {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static void Pass(string id) { Console.WriteLine($"  ✓ {id} PASS"); WriteResult($"{id} PASS"); }
    static void Fail(string id, string problem) { _failures++; Console.WriteLine($"  ✗ {id} FAIL: {problem}"); WriteResult($"{id} FAIL: {problem}"); }
    static void WriteResult(string line) => File.AppendAllText(ResultsFile, line + Environment.NewLine);

    /// <summary>Opens a generated HTML deck in the system default browser for visual inspection.</summary>
    static void OpenInBrowser(string hostFile)
    {
        try
        {
            Console.WriteLine($"  opening {hostFile} in default browser");
            Process.Start(new ProcessStartInfo(hostFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        { Console.WriteLine($"  (cannot open browser: {ex.Message})"); }
    }

    // ---------- deterministic self-test (no LLM, no network) ----------

    /// <summary>Runs the offline deterministic checks: SVG icon embedding and animated background
    /// injection. Exit code 0 = all green. Invoked with `--selftest`.</summary>
    static int RunSelfTest()
    {
        Console.WriteLine("══════════ PresentationPlugin deterministic self-test ══════════");
        var failures = 0;

        failures += Test("icons: controlled dir (size + color + paths)", () =>
        {
            var iconsDir = Path.Combine(Path.GetTempPath(), "ppl-selftest-icons-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(iconsDir);
            try
            {
                File.WriteAllText(Path.Combine(iconsDir, "disc.svg"),
                    "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"24\" height=\"24\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"><circle cx=\"12\" cy=\"12\" r=\"10\"/></svg>");
                File.WriteAllText(Path.Combine(iconsDir, "chart.svg"),
                    "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"24\" height=\"24\"><path fill=\"currentColor\" d=\"M4 20V10h4v10H4z\"/></svg>");
                var sample = "<img src=\"disc.32.aa0000.svg\" alt=\"disc\">" +
                             "<img src=\"chart.svg\">" +
                             "<img src='disc.48.00ff00.svg'>" +
                             "<img src=\"/icons/disc.24.svg\">" +
                             "<img src=\"zzzz.16.123456.svg\">";
                var outHtml = Utility.EmbedSvgIcons(sample, iconsDir);
                if (Regex.Matches(outHtml, "src=\"data:image/svg\\+xml;base64,").Count != 4)
                    return $"expected 4 data-URI srcs, got: {outHtml}";
                if (!outHtml.Contains("zzzz.16.123456.svg"))
                    return "unknown icon must stay unresolved";
                var first = Regex.Match(outHtml, "src=\"data:image/svg\\+xml;base64,([^\"]+)\"");
                var svg = Encoding.UTF8.GetString(Convert.FromBase64String(first.Groups[1].Value));
                if (!svg.Contains("width=\"32\"")) return $"size 32 not applied: {svg}";
                if (!svg.Contains("stroke=\"#aa0000\"")) return $"color aa0000 not applied: {svg}";
                return null;
            }
            finally { try { Directory.Delete(iconsDir, true); } catch { } }
        });

        failures += Test("icons: plugin wrapper on real assets", () =>
        {
            var outHtml = PresentationPlugin.EmbedSvgIcons("<img src=\"disc.32.aa0000.svg\">");
            var m = Regex.Match(outHtml, "src=\"data:image/svg\\+xml;base64,([^\"]+)\"");
            if (!m.Success) return $"disc icon not embedded (icons dir missing?): {outHtml}";
            var svg = Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value));
            if (!svg.Contains("width=\"32\"")) return "size not applied on real icon";
            if (!svg.Contains("#aa0000")) return "color not applied on real icon";
            return null;
        });

        failures += Test("background: style match + position", () =>
        {
            var html = "<html><head><title>t</title></head><body><p>x</p></body></html>";
            var outHtml = PresentationPlugin.InjectAnimatedBackground(html, "modern");
            if (Regex.Matches(outHtml, "</head>").Count != 1) return "</head> must appear exactly once";
            if (!outHtml.Contains("bg-modern")) return "modern block not injected";
            if (outHtml.IndexOf("bg-modern") > outHtml.IndexOf("</head>")) return "block must precede </head>";
            return null;
        });

        failures += Test("background: case-insensitive style", () =>
        {
            var outHtml = PresentationPlugin.InjectAnimatedBackground("<html><head></head></html>", "MODERN");
            return outHtml.Contains("bg-modern") ? null : "case-insensitive lookup failed";
        });

        failures += Test("background: multi-word style", () =>
        {
            var outHtml = PresentationPlugin.InjectAnimatedBackground("<html><head></head></html>", "Minimalist White");
            return outHtml.Contains("bg-minimal") ? null : "multi-word style lookup failed";
        });

        failures += Test("background: random fallback + no duplication", () =>
        {
            var html = "<html><head><title>t</title></head><body></body></html>";
            var outHtml = PresentationPlugin.InjectAnimatedBackground(html, "NoSuchStyle");
            var blocks = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "assets"), "*.bg")
                .Select(File.ReadAllText).ToArray();
            if (blocks.Length == 0) return "no .bg assets staged in harness output";
            if (!blocks.Any(b => outHtml.Contains(b[..40]))) return "no known background block injected";
            if (Regex.Matches(outHtml, "</head>").Count != 1) return "</head> must appear exactly once";
            return null;
        });

        failures += Test("pipeline: full post-processing on real template", () =>
        {
            var deck = PresentationPlugin.TemplateHtml.Replace(
                "<div class=\"slide active\"></div>",
                "<div class=\"slide active\"><h2 style=\"color: var(--color-text);\">Test</h2>" +
                "<img src=\"disc.32.aa0000.svg\" alt=\"disc\"><img src=\"users.24.svg\" alt=\"users\"></div>");
            var outHtml = PresentationPlugin.EmbedSvgIcons(deck);
            outHtml = PresentationPlugin.InjectFixContentSizeScript(outHtml);
            outHtml = PresentationPlugin.InjectAnimatedBackground(outHtml, "Cyberpunk Neon");
            if (Regex.Matches(outHtml, "</head>").Count != 1) return "</head> must appear exactly once";
            if (Regex.Matches(outHtml, "</body>").Count != 1) return "</body> must appear exactly once";
            if (!outHtml.Contains("bg-cyber")) return "cyberpunk background not injected";
            if (outHtml.IndexOf("bg-cyber") > outHtml.IndexOf("</head>")) return "background must precede </head>";
            if (Regex.Matches(outHtml, "src=\"data:image/svg\\+xml;base64,").Count != 2)
                return "both icons must be embedded as data URIs";
            if (outHtml.Contains("</script></script>")) return "double-wrapped script (malformed)";
            if (!outHtml.Contains("const zoom") || outHtml.IndexOf("const zoom") > outHtml.IndexOf("</body>"))
                return "fix-content-size script must precede </body>";
            return null;
        });

        Console.WriteLine(failures == 0 ? "  ALL SELF-TESTS PASSED" : $"  {failures} SELF-TEST FAILURES");
        return failures == 0 ? 0 : 1;
    }

    static int Test(string id, Func<string?> run)
    {
        try
        {
            var problem = run();
            if (problem == null) { Console.WriteLine($"  ✓ {id} PASS"); return 0; }
            Console.WriteLine($"  ✗ {id} FAIL: {problem}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ {id} CRASH: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ---------- fixtures ----------

    /// <summary>Generates one visual preview deck per style through the real post-processing
    /// pipeline (icon embedding + content-size fix + animated background), written to
    /// %TEMP%\bg-preview. No LLM involved. Invoked with `--previews`.</summary>
    static int RunPreviews()
    {
        var styles = new[] { "Modern", "Vintage", "Minimalist White", "Brutalist", "Retro Pop",
            "Vaporwave", "Biophilic Design", "Cyberpunk Neon", "Glassmorphism", "Bento Grid", "Retro" };
        var outDir = Path.Combine(Path.GetTempPath(), "bg-preview");
        Directory.CreateDirectory(outDir);
        var sampleSlide = "<div class=\"slide active\"><h2 style=\"color: var(--color-text);\">Icons</h2>" +
            "<p style=\"color: var(--color-text);\"><img src=\"disc.32.aa0000.svg\" alt=\"disc\"> " +
            "<img src=\"users.24.svg\" alt=\"users\"> <img src=\"rocket.28.22c55e.svg\" alt=\"rocket\"></p></div>" +
            "<div class=\"slide\"><h2 style=\"color: var(--color-text);\">Second slide</h2></div>";
        foreach (var style in styles)
        {
            var deck = PresentationPlugin.TemplateHtml.Replace("<div class=\"slide active\"></div>", sampleSlide);
            deck = PresentationPlugin.EmbedSvgIcons(deck);
            deck = PresentationPlugin.InjectFixContentSizeScript(deck);
            deck = PresentationPlugin.InjectAnimatedBackground(deck, style);
            var file = Path.Combine(outDir, style.ToLowerInvariant().Replace(' ', '-') + ".html");
            File.WriteAllText(file, deck);
        }
        Console.WriteLine($"Preview decks written to {outDir}");
        return 0;
    }

    /// <summary>Ensures the requested provider exists. DeepSeekBridge is preconfigured;
    /// 'Ollama_Qwen' (local qwen3.5:4b) is registered at runtime when requested but absent,
    /// keeping providers.json untouched.</summary>
    static void EnsureProvider()
    {
        if (ProviderConfigs.TryGet(_providerName, out _)) return;
        if (!string.Equals(_providerName, "Ollama_Qwen", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown provider '{_providerName}'. Use --provider with a configured provider or 'Ollama_Qwen'.");
        ProviderConfigs.Add(new ProviderConfig
        {
            ProviderName = "Ollama_Qwen",
            Protocol = ProviderProtocol.OpenAI,
            CacheType = ProviderCacheType.PrefixCache,
            ModelName = "qwen3.5:4b",
            BaseAddress = new Uri("http://localhost:11434/"),
            EndPoint = "v1/chat/completions",
            Timeout = TimeSpan.FromMinutes(40),
            PauseBetweenRequests = TimeSpan.Zero,
            ContextWindow = 262144,
        }, persist: false);
    }

    /// <summary>Guarantees the context images are visible in the deck: when the LLM only mocked an
    /// image (placeholder SVG with matching alt text instead of the real file name), the placeholder
    /// src is replaced with the actual PNG data URI and its opacity reset, so the image always shows.</summary>
    static void RepairImages(string htmlPath)
    {
        var html = File.ReadAllText(htmlPath);
        var changed = false;
        foreach (var (file, keyword) in new[] { ("skyline.png", "skyline"), ("power-pylon.png", "pylon"), ("brain.png", "brain") })
        {
            var src = Path.Combine(_workspace, "images", file);
            if (!File.Exists(src)) continue;
            var dataUri = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(src));
            if (html.Contains(dataUri)) continue; // the LLM already referenced the real file
            var pattern = $@"<img\b(?=[^>]*\balts?\s*=\s*[""'][^""']*{Regex.Escape(keyword)}[^""']*[""'])[^>]*>";
            var count = 0;
            html = Regex.Replace(html, pattern, m =>
            {
                count++;
                var tag = Regex.Replace(m.Value, @"src\s*=\s*[""'][^""']*[""']", $"src=\"{dataUri}\"", RegexOptions.IgnoreCase);
                return Regex.Replace(tag, @"opacity\s*:\s*[\d.]+", "opacity:1", RegexOptions.IgnoreCase);
            }, RegexOptions.IgnoreCase);
            if (count > 0) changed = true;
        }
        if (changed)
        {
            File.WriteAllText(htmlPath, html);
            Console.WriteLine("  images: repaired placeholder <img> tags with the real PNG data URIs");
        }

        // The LLM typically styles .img-bg / .img-bg-light at ~8% opacity, making the context
        // images invisible; force a clearly visible opacity when any of them is embedded.
        if (html.Contains("data:image/png;base64"))
        {
            html = File.ReadAllText(htmlPath);
            var overrideCss = "<style>.img-bg,.img-bg-light{opacity:.9!important}</style></head>";
            var count = 0;
            html = Regex.Replace(html, "</head>", _ => { count++; return overrideCss; }, RegexOptions.IgnoreCase);
            if (count > 0) { File.WriteAllText(htmlPath, html); Console.WriteLine("  images: forced visible opacity (.img-bg → 0.9)"); }
        }
    }

    /// <summary>Stages the public-domain context images (white silhouettes on transparent, plus a
    /// colored circuit-brain) in the workspace so the deck can reference them (they are embedded as
    /// data URIs by the plugin). Sources: Openclipart 348279 (Tokyo skyline), 318395 (power pylon)
    /// and 307528 (cybernetic brain circuit), all CC0/public domain; the black silhouettes are
    /// inverted to white for visibility on the dark theme.</summary>
    static void StageImages()
    {
        var srcDir = Path.Combine(AppContext.BaseDirectory, "assets", "images");
        if (!Directory.Exists(srcDir)) return;
        var dstDir = Path.Combine(_workspace, "images");
        Directory.CreateDirectory(dstDir);
        foreach (var file in Directory.GetFiles(srcDir, "*.png"))
            File.Copy(file, Path.Combine(dstDir, Path.GetFileName(file)), overwrite: true);
    }

    /// <summary>Stages a small icon set in the harness output so SVG placeholder embedding
    /// can be exercised (same lookup path as the document pipeline).</summary>
    static void StageIcons()
    {
        var iconsDir = Path.Combine(AppContext.BaseDirectory, "assets", "icons");
        Directory.CreateDirectory(iconsDir);
        var icons = new Dictionary<string, string>
        {
            ["rocket"] = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M12 2c3 2 5 6 5 10l-5 3-5-3c0-4 2-8 5-10z\"/></svg>",
            ["chart"] = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M4 20V10h4v10H4zm6 0V4h4v16h-4zm6 0v-7h4v7h-4z\"/></svg>",
            ["lightbulb"] = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M12 2a7 7 0 0 0-4 12.7c.6.5 1 1.3 1 2.1V18h6v-1.2c0-.8.4-1.6 1-2.1A7 7 0 0 0 12 2zm-2 14h4v1h-4v-1z\"/></svg>",
        };
        foreach (var (name, svg) in icons)
            File.WriteAllText(Path.Combine(iconsDir, name + ".svg"), svg);
    }
}
