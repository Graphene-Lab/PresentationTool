using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AIOrchestrator;
using AIOrchestrator.API;
using UISupportGeneric;

namespace PresentationToolHarness;

/// <summary>
/// PresentationTool single-create visual test: generates ONE presentation and opens it
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
    private static readonly string ResultsFile = Path.Combine(Path.GetTempPath(), "presentationtool_test_results.txt");

    static int Main(string[] args)
    {
        var idx = Array.IndexOf(args, "--provider");
        if (idx >= 0 && idx + 1 < args.Length) _providerName = args[idx + 1];
        if (Array.IndexOf(args, "--selftest") >= 0) return RunSelfTest();
        if (Array.IndexOf(args, "--previews") >= 0) return RunPreviews();
        if (Array.IndexOf(args, "--examples") >= 0) return RunExamples(args);
        EnsureProvider();

        Log.IsEnabled = true;
        _workspace = Path.Combine(Path.GetTempPath(), "PresentationTool.Tests-workspace");
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

        Console.WriteLine("══════════ PresentationTool single-create test ══════════");
        Console.WriteLine($"provider: {_providerName}");
        Log.LogStep($"=== PresentationTool single-create test (provider {_providerName}) ===");

        try
        {
            var tool = new PresentationTool();
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
        Console.WriteLine("══════════ PresentationTool deterministic self-test ══════════");
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
                if (!svg.Contains("width=\"32px\"")) return $"size 32 not applied: {svg}";
                if (!svg.Contains("stroke=\"#aa0000\"")) return $"color aa0000 not applied: {svg}";
                return null;
            }
            finally { try { Directory.Delete(iconsDir, true); } catch { } }
        });

        failures += Test("icons: plugin wrapper on real assets", () =>
        {
            var outHtml = PresentationTool.EmbedSvgIcons("<img src=\"disc.32.aa0000.svg\">");
            var m = Regex.Match(outHtml, "src=\"data:image/svg\\+xml;base64,([^\"]+)\"");
            if (!m.Success) return $"disc icon not embedded (icons dir missing?): {outHtml}";
            var svg = Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value));
            if (!svg.Contains("width=\"32px\"")) return "size not applied on real icon";
            if (!svg.Contains("#aa0000")) return "color not applied on real icon";
            return null;
        });

        failures += Test("background: style match + position", () =>
        {
            var html = "<html><head><title>t</title></head><body><p>x</p></body></html>";
            var outHtml = PresentationTool.InjectAnimatedBackground(html, "modern");
            if (Regex.Matches(outHtml, "</head>").Count != 1) return "</head> must appear exactly once";
            if (!outHtml.Contains("bg-modern")) return "modern block not injected";
            if (outHtml.IndexOf("bg-modern") > outHtml.IndexOf("</head>")) return "block must precede </head>";
            return null;
        });

        failures += Test("background: case-insensitive style", () =>
        {
            var outHtml = PresentationTool.InjectAnimatedBackground("<html><head></head></html>", "MODERN");
            return outHtml.Contains("bg-modern") ? null : "case-insensitive lookup failed";
        });

        failures += Test("background: multi-word style", () =>
        {
            var outHtml = PresentationTool.InjectAnimatedBackground("<html><head></head></html>", "Minimalist White");
            return outHtml.Contains("bg-minimal") ? null : "multi-word style lookup failed";
        });

        failures += Test("background: random fallback + no duplication", () =>
        {
            var html = "<html><head><title>t</title></head><body></body></html>";
            var outHtml = PresentationTool.InjectAnimatedBackground(html, "NoSuchStyle");
            var blocks = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "assets"), "*.bg")
                .Select(File.ReadAllText).ToArray();
            if (blocks.Length == 0) return "no .bg assets staged in harness output";
            if (!blocks.Any(b => outHtml.Contains(b[..40]))) return "no known background block injected";
            if (Regex.Matches(outHtml, "</head>").Count != 1) return "</head> must appear exactly once";
            return null;
        });

        failures += Test("pipeline: full post-processing on real template", () =>
        {
            var deck = PresentationTool.TemplateHtml.Replace(
                "<div class=\"slide active\"></div>",
                "<div class=\"slide active\"><h2 style=\"color: var(--color-text);\">Test</h2>" +
                "<img src=\"disc.32.aa0000.svg\" alt=\"disc\"><img src=\"users.24.svg\" alt=\"users\"></div>");
            var outHtml = PresentationTool.EmbedSvgIcons(deck);
            outHtml = PresentationTool.InjectFixContentSizeScript(outHtml);
            outHtml = PresentationTool.InjectAnimatedBackground(outHtml, "Cyberpunk Neon");
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

        failures += Test("definitions: dynamic style list resolves", () =>
        {
            var defs = Analyzer.GetToolDefinitions(typeof(PresentationTool));
            if (defs.Contains("[[available_styles]]")) return "placeholder not resolved";
            if (!defs.Contains("Cyberpunk Neon")) return "resolved style list missing an entry";
            return null;
        });

        failures += Test("versioning: snapshot + rollback on .html", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "ptl-restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var saved = Setup.DocumentsPath;
            Setup.DocumentsPath = dir;
            try
            {
                var f = Path.Combine(dir, "deck.html");
                File.WriteAllText(f, "<html><body><h1>V1</h1></body></html>");
                var v1 = GitSupport.Snapshot(f, "V1");
                File.WriteAllText(f, "<html><body><h1>V2</h1></body></html>");
                GitSupport.Snapshot(f, "V2");
                var history = GitSupport.History(f);
                if (history.Count != 2) return $"history count: {history.Count}";
                // Pending edit (NOT yet versioned): the restore swap must capture it before overwriting.
                File.WriteAllText(f, "<html><body><h1>V3</h1></body></html>");
                var r = GitSupport.Restore(v1, f);
                if (!r.StartsWith("Restored")) return $"restore result: {r}";
                if (!File.ReadAllText(f).Contains("V1")) return "restored content is not V1";
                var v3 = GitSupport.History(f)[0].VersionId;   // the swap snapshot of the pending V3
                if (GitSupport.History(f).Count != 3) return "history count after restore != 3";
                GitSupport.Restore(v3, f);                      // rollback of the rollback
                if (!File.ReadAllText(f).Contains("V3")) return "swap snapshot not restorable";
                return null;
            }
            finally { Setup.DocumentsPath = saved; try { Directory.Delete(dir, true); } catch { } }
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
            var deck = PresentationTool.TemplateHtml.Replace("<div class=\"slide active\"></div>", sampleSlide);
            deck = PresentationTool.EmbedSvgIcons(deck);
            deck = PresentationTool.InjectFixContentSizeScript(deck);
            deck = PresentationTool.InjectAnimatedBackground(deck, style);
            var file = Path.Combine(outDir, style.ToLowerInvariant().Replace(' ', '-') + ".html");
            File.WriteAllText(file, deck);
        }
        Console.WriteLine($"Preview decks written to {outDir}");
        return 0;
    }

    /// <summary>Generates the four book example decks (pitch, sales, training, board review)
    /// with the real tool and copies the filled HTML to the AgentBridge examples assets folder.
    /// Invoked with `--examples`.</summary>
    static int RunExamples(string[] args)
    {
        EnsureProvider();
        Log.IsEnabled = true;
        _workspace = Path.Combine(Path.GetTempPath(), "PresentationTool.Examples-workspace");
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); } catch { }
        Directory.CreateDirectory(_workspace);
        Setup.SkipIndexingOnStartup = true;
        Setup.DocumentsPath = _workspace;
        Setup.ProviderConfig = ProviderConfigs.Get(_providerName);
        StageIcons();
        var outDir = @"C:\Users\andre\OneDrive\Sorgenti\AgentBridge\examples\_assets\pt-html";
        Directory.CreateDirectory(outDir);
        var tool = new PresentationTool();
        var specs = new (string id, string desc, string style, string ctx)[]
        {
            ("pitch-deck",
             "Create an investor pitch deck for 'GreenGrid', a startup that uses AI to cut energy use in commercial buildings. " +
             "8 slides: cover, the problem, the solution, the market opportunity, the business model, traction so far, the team, the ask.",
             "Cyberpunk Neon",
             "GreenGrid was founded in 2023 in Milan by Marco Rossi (CEO, ex-Enel) and Anna Bianchi (CTO, ex-Politecnico AI lab). " +
             "Problem: commercial buildings waste up to 35% of their energy on heating, cooling and lighting that runs when no one needs it. " +
             "Solution: the GreenGrid platform connects to a building's sensors and HVAC and uses AI to cut energy use by up to 30% with no hardware replacement. " +
             "Market: European building energy management is EUR 18B in 2026, growing 16% a year. " +
             "Business model: SaaS priced per square metre, EUR 0.35/m2/month, plus a one-off setup fee. " +
             "Traction: 28 paying buildings, EUR 480k ARR, 190% year over year growth, 96% customer retention. " +
             "Team: 14 people across engineering, sales and customer success. " +
             "The ask: EUR 3M seed to reach EUR 3M ARR in 24 months and expand to Germany and Spain."),

            ("sales-presentation",
             "Create a B2B sales presentation for 'CloudVault', a managed cloud backup service for small businesses. " +
             "7 slides: cover, the data-loss problem, how CloudVault works, key features, return on investment, customer proof, next steps.",
             "Modern",
             "CloudVault is a fully managed cloud backup service for small and medium businesses. " +
             "Problem: 60% of small businesses that lose their data for more than 10 days never reopen; ransomware attacks rose 40% last year. " +
             "How it works: install an agent, it encrypts and backs up files, databases and servers automatically every hour, with one-click restore. " +
             "Features: end-to-end encryption, 30-day version history, geo-redundant storage, 24/7 monitoring, guided restore. " +
             "ROI: average customer saves EUR 12,000 a year versus running their own backup servers; full recovery in under 2 hours. " +
             "Customer proof: 1,400 businesses protected, 4.8/5 satisfaction, case study of a 40-person accounting firm recovered from ransomware in 90 minutes. " +
             "Pricing: from EUR 49/month per company. Next step: a free 30-day trial with a guided setup call."),

            ("training-deck",
             "Create an internal onboarding training deck for new customer-support agents at a software company. " +
             "8 slides: welcome, our product in one page, our tone of voice, the ticket workflow, how to escalate, the tools we use, best practices, and a summary with key takeaways.",
             "Minimalist White",
             "Company: Nimbus, a project-management SaaS used by 8,000 teams. " +
             "Product: Nimbus helps teams plan projects, assign tasks, track progress and share documents in one workspace. " +
             "Tone of voice: friendly, clear, concise, never jargon; always thank the customer and confirm the fix. " +
             "Ticket workflow: a ticket arrives, acknowledge within 15 minutes, diagnose, solve or escalate, confirm with the customer, close with a satisfaction survey. " +
             "Escalation: if unresolved in 30 minutes or the customer is upset, escalate to a senior agent with a clear summary. " +
             "Tools: the helpdesk console, the internal knowledge base, a status page, and a chat with the engineering team. " +
             "Best practices: search the knowledge base first, never promise a date you cannot keep, log every step, aim for a CSAT of 4.5 or higher. " +
             "Key takeaways: respond fast, stay friendly, document everything, escalate early."),

            ("board-review",
             "Create a quarterly board review deck for a SaaS company covering Q2 2026. " +
             "8 slides: cover and agenda, key metrics, revenue detail, churn and retention, product updates, hiring and team, risks, and priorities for next quarter.",
             "Bento Grid",
             "Quarter: Q2 2026 (April to June). " +
             "Key metrics: ARR EUR 4.2M (up 12% quarter over quarter), 620 customers, net revenue retention 112%, gross margin 78%. " +
             "Revenue detail: new ARR EUR 620k, expansion ARR EUR 180k, contraction EUR 40k; average deal size EUR 8,400. " +
             "Churn: gross logo churn 1.8% per quarter, down from 2.4%; main churn reason is small accounts consolidating tools. " +
             "Product: shipped a mobile app, a reporting dashboard, and a public API; 30% of usage now mobile. " +
             "Hiring: grew from 48 to 57 people; added 4 engineers and 3 customer-success managers; two senior roles still open. " +
             "Risks: longer sales cycles in the enterprise segment, a competitor cutting prices, and key-person risk in the platform team. " +
             "Priorities for Q3: launch the enterprise tier, close the two senior hires, reduce churn below 1.5%, and prepare the Series B."),
        };
        int ok = 0;
        foreach (var s in specs)
        {
            var dest = Path.Combine(outDir, s.id + ".html");
            if (File.Exists(dest) && Array.IndexOf(args, "--force") < 0)
            { Console.WriteLine($"  SKIP {s.id} (already exists)"); ok++; continue; }
            try
            {
                var r = tool.CreatePresentation(s.desc, style: s.style, contextText: s.ctx, saveFullNameFile: "/" + s.id + ".html");
                var f = Path.Combine(_workspace, s.id + ".html");
                if (File.Exists(f))
                {
                    File.Copy(f, dest, overwrite: true);
                    Console.WriteLine($"  OK {s.id} ({s.style}) -> {outDir}");
                    ok++;
                }
                else Console.WriteLine($"  FAIL {s.id}: {r}");
            }
            catch (Exception ex) { Console.WriteLine($"  CRASH {s.id}: {ex.GetType().Name}: {ex.Message}"); }
        }
        Console.WriteLine($"examples: {ok}/{specs.Length} decks written");
        return ok == specs.Length ? 0 : 1;
    }

    /// <summary>Ensures the requested provider exists. DeepSeekBridge is preconfigured;
    /// 'Ollama_Qwen' (local qwen3.5:4b) is registered at runtime when requested but absent,
    /// keeping providers.json untouched.</summary>
    static void EnsureProvider()
    {
        if (ProviderConfigs.TryGet(_providerName, out _)) return;
        if (string.Equals(_providerName, "SuperFast_Flash", StringComparison.OrdinalIgnoreCase))
        {
            ProviderConfigs.Add(new ProviderConfig
            {
                ProviderName = "SuperFast_Flash",
                Protocol = ProviderProtocol.OpenAI,
                CacheType = ProviderCacheType.PrefixCache,
                ModelName = "halogen-qwen3.8-flash-next",
                ApiKey = Environment.GetEnvironmentVariable("SUPERFAST_FLASH_API_KEY") ?? "",
                BaseAddress = new Uri("http://192.168.43.253:8741/"),
                EndPoint = "v1/chat/completions",
                NoJsonResponseFormat = true,
                ExtraBody = new Dictionary<string, object> { ["reasoning_effort"] = "low" },
                Timeout = TimeSpan.FromMinutes(30),
                PauseBetweenRequests = TimeSpan.Zero,
                ContextWindow = 262144,
            }, persist: false);
            return;
        }
        if (!string.Equals(_providerName, "Ollama_Qwen", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown provider '{_providerName}'. Use --provider with a configured provider, 'Ollama_Qwen' or 'SuperFast_Flash'.");
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
