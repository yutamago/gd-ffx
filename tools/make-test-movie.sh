#!/usr/bin/env bash
# Makes an original test video for R7's movie checks (docs/remaster-editor.md,
# "Checks"): colour bars, a moving bar and the frame number, with a tone that
# changes every second, 640 x 360 at 30 fps, as Ogg Theora with Vorbis sound,
# written by Godot's own movie writer (--write-movie) from a tiny project made
# here in a temporary folder. Nothing of the game's; generated, so no licence
# question (the script is the source). Needs Xvfb (the writer renders).
#
#   tools/make-test-movie.sh OUT.ogv [SECONDS]
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
out="${1:?OUT.ogv}"
seconds="${2:-8}"
case "$out" in /*) ;; *) out="$PWD/$out" ;; esac
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cat >"$tmp/project.godot" <<'EOF'
config_version=5

[application]
config/name="r7-test-pattern"
run/main_scene="res://pattern.tscn"

[rendering]
renderer/rendering_method="gl_compatibility"
EOF
cat >"$tmp/pattern.gd" <<'EOF'
extends Node2D
# An original test pattern: colour bars, a moving bar, the frame number; a tone of 440 Hz and
# 660 Hz in turn, a second each.
var frame := 0
var gen: AudioStreamGeneratorPlayback
var phase := 0.0
func _ready():
	var p := AudioStreamPlayer.new()
	var g := AudioStreamGenerator.new()
	g.mix_rate = 48000
	p.stream = g
	add_child(p)
	p.play()
	gen = p.get_stream_playback()
func _process(_d):
	frame += 1
	queue_redraw()
	var hz := 440.0 if (frame / 30) % 2 == 0 else 660.0
	while gen.get_frames_available() > 0:
		var s := sin(phase) * 0.25
		gen.push_frame(Vector2(s, s))
		phase += TAU * hz / 48000.0
func _draw():
	var cols := [Color.WHITE, Color.YELLOW, Color.CYAN, Color.GREEN, Color.MAGENTA, Color.RED, Color.BLUE]
	var w := 640.0 / cols.size()
	for i in cols.size():
		draw_rect(Rect2(i * w, 0, w, 260), cols[i])
	draw_rect(Rect2(0, 260, 640, 100), Color(0.1, 0.1, 0.1))
	draw_rect(Rect2(float(frame * 8 % 640), 260, 24, 100), Color.WHITE)
	draw_string(ThemeDB.fallback_font, Vector2(20, 340), "R7 test pattern  frame %d" % frame, HORIZONTAL_ALIGNMENT_LEFT, -1, 32)
EOF
cat >"$tmp/pattern.tscn" <<'EOF'
[gd_scene load_steps=2 format=3]

[ext_resource type="Script" path="res://pattern.gd" id="1"]

[node name="Pattern" type="Node2D"]
script = ExtResource("1")
EOF
frames=$((seconds * 30))
env -u WAYLAND_DISPLAY -u DISPLAY timeout 300 "$repo/tools/capped.sh" xvfb-run -a -s "-screen 0 640x360x24" \
    "$godot" --path "$tmp" --display-driver x11 --rendering-driver opengl3 --audio-driver Dummy \
    --resolution 640x360 --write-movie "$out" --fixed-fps 30 --quit-after "$frames" >"$tmp/log" 2>&1 || {
    tail -20 "$tmp/log" >&2
    exit 1
}
echo "make-test-movie: $out ($seconds s)"
