# HDRIs for the Materials tab's preview

Four environment maps for the material workbench's HDRI lighting
(docs/remaster-materials.md, "The preview"), 1k (1024 x 512) Radiance `.hdr`
files, downloaded unchanged from Poly Haven on 2026-11-18. Each is licensed
**CC0 1.0** (public domain dedication, no attribution required:
https://polyhaven.com/license and
https://creativecommons.org/publicdomain/zero/1.0/); credited here anyway.

| File | Kind | Source | Authors |
|---|---|---|---|
| `meadow_2_1k.hdr` | sunny outdoor | https://polyhaven.com/a/meadow_2 | Sergej Majboroda |
| `kloofendal_overcast_1k.hdr` | overcast outdoor | https://polyhaven.com/a/kloofendal_overcast | Greg Zaal |
| `venice_sunset_1k.hdr` | sunset | https://polyhaven.com/a/venice_sunset | Greg Zaal |
| `lebombo_1k.hdr` | interior | https://polyhaven.com/a/lebombo | Greg Zaal |

MD5 of the files as downloaded: meadow_2 `7deac04bbf250f12a4daf1afaa6cab5f`,
kloofendal_overcast `702c39d6253f4e482fa151edc03f1728`, venice_sunset
`a5ec4d30fc3421c214a17b208fb66d5f`, lebombo `eb75d6df7d824e83940e2c7f2ef65c70`.

They are editor-only, like the plugin: exports leave out `addons/*`
(`export_presets.cfg`, checked by `tools/export-files.py check`). Your own
HDRIs (`.hdr`, `.exr`) go in any folder named by the editor setting
`ffx_remaster/hdri_dir`; the tab lists them after these.
