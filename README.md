# PresentationTool

Self-contained HTML presentation (16:9 PowerPoint-style deck) agent tool for AIOrchestrator.
The LLM designs the deck from a description and the plugin produces a **single self-contained
HTML file**: styling, animated background, charts, SVG icons and images are embedded in it.

## Methods

| Method | Purpose |
|---|---|
| `create_presentation` | Creates a new deck from a description (optional style, context text/file, image files, output path, language). |
| `update_presentation` | Applies requested changes to an existing deck in place (numbered `.NNN.bak` backup before overwriting, backup name returned). |

## Usage

Loads as a plugin (see [AGENT_TOOLS_GUIDE.md](https://github.com/Graphene-Lab/AgentHarness/blob/master/API/AGENT_TOOLS_GUIDE.md)):
drop the `dll` + `xml` into the host's `Tools/` folder, or let the host build it via its
`BuildToolPlugins` target. The tool is auto-updatable from NuGet (`Graphene.PresentationTool`).

### create_presentation

```
create_presentation(description, style?, outputTwoLetterLanguage?, contextText?, contextFile?, imageFiles?, saveFullNameFile?)
```

- `description` (required): subject, descriptive title and purpose of the presentation.
- `style` (optional): graphic style that shapes the deck (e.g. Modern, Cyberpunk Neon, ...).
- `outputTwoLetterLanguage` (optional): two-letter language code (e.g. "en", "fr"); auto-detected from the context when omitted.
- `contextText` / `contextFile` (optional): supporting material the deck content must be based on.
- `imageFiles` (optional): workspace images embedded into the deck (Unix-style paths); the LLM
  places each image, each is used at most once. Each image is shown to the LLM via the unified
  `FileManager.GetFilesInfo` block: path + size + `Classification:` + YOLO `Metadata:` JSON
  (created and embedded permanently in the image when absent).
- `saveFullNameFile` (optional): output path/name (`.html`, Unix-style). Default:
  `/presentation/presentation_yyyyMMdd_HHmmss.html` in the workspace.

### update_presentation

```
update_presentation(filePath, changes, contextText?, imageFiles?)
```

Reads the current deck HTML, validates that the changes are clear, has the LLM apply them
literally and overwrites the file in place. A numbered `.NNN.bak` backup is created before
the deck is overwritten and its name is returned so the agent can restore it later.

## Packaging

Date-based auto versioning (`1.yy.MM.dd`) and CI publish to NuGet on every push to
`master` — see `.github/workflows/publish.yml` and the plugin creation guide in
AGENT_TOOLS_GUIDE.md.
