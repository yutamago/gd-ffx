# Writes the Godot engine's licence and its third-party notices, as the
# engine reports them (Engine.get_license_text, get_copyright_info,
# get_license_info), for an export's licences folder (tools/export.sh).
#
#   godot --headless --script tools/godot-licences.gd -- OUT_DIR
#
# OUT_DIR/godot-LICENSE.txt: Godot's MIT licence; OUT_DIR/godot-COPYRIGHT.txt:
# the components the engine contains, their copyright holders and licences,
# then the full text of each licence.
extends SceneTree


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() != 1:
		printerr("usage: godot --headless --script godot-licences.gd -- OUT_DIR")
		quit(2)
		return
	var out: String = args[0]
	var v := Engine.get_version_info()
	var lines := PackedStringArray()
	lines.append("Godot Engine %s: copyright and third-party notices" % v.string)
	lines.append("(from the engine itself: Engine.get_copyright_info and get_license_info)")
	lines.append("")
	for component in Engine.get_copyright_info():
		lines.append("-".repeat(72))
		lines.append(component.name)
		for part in component.parts:
			lines.append("  files: %s" % ", ".join(part.files))
			for holder in part.copyright:
				lines.append("  Copyright %s" % holder)
			lines.append("  licence: %s" % part.license)
		lines.append("")
	lines.append("")
	lines.append("Licence texts")
	lines.append("=".repeat(40))
	var texts := Engine.get_license_info()
	var names := texts.keys()
	names.sort()
	for name in names:
		lines.append("")
		lines.append("-".repeat(72))
		lines.append(name)
		lines.append("-".repeat(72))
		lines.append("")
		lines.append(texts[name])
	var ok := _write(out.path_join("godot-LICENSE.txt"), Engine.get_license_text())
	ok = ok and _write(out.path_join("godot-COPYRIGHT.txt"), "\n".join(lines) + "\n")
	quit(0 if ok else 1)


func _write(path: String, text: String) -> bool:
	var f := FileAccess.open(path, FileAccess.WRITE)
	if f == null:
		printerr("cannot write %s: %s" % [path, error_string(FileAccess.get_open_error())])
		return false
	f.store_string(text)
	return true
