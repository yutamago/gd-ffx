# AI map workflows (an example template)

`example-map-workflow.json` is a template for the Materials tab's AI estimation
(docs/remaster-materials.md, "AI estimation"), written for gd-ffx: it is not a
working workflow. Node 2 (`GdFfxPlaceholder_YourMapEstimator`) stands for the
nodes of your own workflow that make the map (a normal, depth or material
estimator you installed in your ComfyUI, under its own licence). gd-ffx ships
no model and no third-party workflow.

To make one of your own: build the workflow in ComfyUI, then Workflow, Export
(API), and in the exported JSON put:

- `%gdffx_input%` where the texture's image name goes (a `LoadImage` node's
  `image`; required);
- `%gdffx_output%` as the `filename_prefix` of the `SaveImage` node whose first
  image is the map (required: that node is the output);
- `%gdffx_map%` where the map's name helps (optional: `albedo`, `height`,
  `normal` or `roughness`).

Then, in Editor Settings, `ffx_remaster/ai_<map>_workflow` (the file),
`ffx_remaster/ai_<map>_model` (the model's name, recorded) and
`ffx_remaster/ai_<map>_licence` (the licence of the model or models it runs:
required, a workflow without one is not run). The maps expected: albedo
(sRGB, without painted light), height (bright is high), normal (OpenGL, green
up), roughness (bright is rough).

Licence: this template is gd-ffx's own (MIT OR Apache-2.0).
