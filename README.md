# PresentationPlugin

PowerPoint (PPTX) agent tool for AIOrchestrator. The LLM designs the deck — slides,
titles, bullets, image placement — and the plugin builds the `.pptx` file with
DocumentFormat.OpenXml.

## Methods

| Method | Purpose |
|---|---|
| `create_presentation` | Creates a new PPTX deck from a description (optional style, context text/file, image files, output path). |
| `fix_presentation` | Applies requested changes to an existing PPTX deck (backup created before overwriting). |

## Usage

Loads as a plugin (see [AGENT_TOOLS_GUIDE.md](https://github.com/Graphene-Lab/AgentHarness/blob/master/API/AGENT_TOOLS_GUIDE.md)):
drop the `dll` + `xml` into the host's `Tools/` folder, or let the host build it via its
`BuildToolPlugins` target. The tool is auto-updatable from NuGet (`Graphene.PresentationPlugin`).

### create_presentation

```
create_presentation(description, style?, contextText?, contextFile?, imageFiles?, savePath?)
```

- `description` (required): what the deck must cover — audience, tone, length.
- `style` (optional): visual/graphic style hint that shapes the LLM's content wording.
- `contextText` (optional): free text the content must be based on.
- `contextFile` (optional): workspace file read as context (Unix-style path, e.g. `/docs/report.md`).
- `imageFiles` (optional): workspace images embedded into slides (Unix-style paths); the LLM
  decides which slide gets which image.
- `savePath` (optional): output path/name (`.pptx`, Unix-style). Default:
  `/presentation_yyyyMMdd_HHmmss.pptx` in the workspace root.

### fix_presentation

```
fix_presentation(filePath, changes, contextText?)
```

Extracts the current deck outline, has the LLM apply `changes`, and updates the deck in
place (titles/bullets; existing slides keep their layout, images and formatting; extra
slides are appended, missing ones removed). A numbered `.NNN.bak` backup is created
before the deck is overwritten.

## Packaging

Date-based auto versioning (`1.yy.MM.dd`) and CI publish to NuGet on every push to
`master` — see `.github/workflows/publish.yml` and the plugin creation guide in
AGENT_TOOLS_GUIDE.md.
