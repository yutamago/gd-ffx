//! The remaster's Flash movies (`ffx_data/gamedata/ps3data/flash/*.swf`),
//! drawn without a Flash player: their bitmaps, and the vector art of a
//! movie's timeline (shapes, morph shapes, sprites) rasterised to RGBA at a
//! chosen scale, for gd-ffx's native screens (docs/architecture.md, "The
//! Flash screens"). Read from the player's archive at run time, never
//! stored.
//!
//! What is drawn: solid, linear, radial and focal (as radial) gradient and
//! bitmap fills, strokes (round joins), morph shapes at a ratio, the
//! display list of every sprite (place, move, replace, remove; nested
//! sprites loop from the frame they were placed), matrices and colour
//! transforms. Not drawn: filters (`PlaceObject3`'s blur and glow), masks
//! (clip depths), blend modes, text, scripts (a sprite always plays).

// the maths' usual names (x, y, a, b, c, d, t, u, w, h)
#![allow(clippy::many_single_char_names, clippy::similar_names)]

use std::collections::{BTreeMap, HashMap};
use std::io::Read as _;

/// A 2D affine matrix: `x' = a x + c y + tx`, `y' = b x + d y + ty`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Matrix {
    pub a: f64,
    pub b: f64,
    pub c: f64,
    pub d: f64,
    pub tx: f64,
    pub ty: f64,
}

impl Matrix {
    pub const IDENTITY: Self = Self {
        a: 1.0,
        b: 0.0,
        c: 0.0,
        d: 1.0,
        tx: 0.0,
        ty: 0.0,
    };

    /// A scale.
    #[must_use]
    pub const fn scale(s: f64) -> Self {
        Self {
            a: s,
            b: 0.0,
            c: 0.0,
            d: s,
            tx: 0.0,
            ty: 0.0,
        }
    }

    fn from_swf(m: &swf::Matrix) -> Self {
        Self {
            a: m.a.to_f64(),
            b: m.b.to_f64(),
            c: m.c.to_f64(),
            d: m.d.to_f64(),
            tx: f64::from(m.tx.get()),
            ty: f64::from(m.ty.get()),
        }
    }

    /// `self` after `inner` (`self * inner`).
    #[must_use]
    pub fn then(&self, inner: &Self) -> Self {
        Self {
            a: self.a * inner.a + self.c * inner.b,
            b: self.b * inner.a + self.d * inner.b,
            c: self.a * inner.c + self.c * inner.d,
            d: self.b * inner.c + self.d * inner.d,
            tx: self.a * inner.tx + self.c * inner.ty + self.tx,
            ty: self.b * inner.tx + self.d * inner.ty + self.ty,
        }
    }

    #[must_use]
    pub fn apply(&self, p: (f64, f64)) -> (f64, f64) {
        (
            self.a * p.0 + self.c * p.1 + self.tx,
            self.b * p.0 + self.d * p.1 + self.ty,
        )
    }

    /// The inverse, or `None` for a singular matrix.
    #[must_use]
    pub fn inverse(&self) -> Option<Self> {
        let det = self.a * self.d - self.b * self.c;
        if det.abs() < 1e-12 {
            return None;
        }
        let (a, b, c, d) = (self.d / det, -self.b / det, -self.c / det, self.a / det);
        Some(Self {
            a,
            b,
            c,
            d,
            tx: -(a * self.tx + c * self.ty),
            ty: -(b * self.tx + d * self.ty),
        })
    }

    fn lerp(&self, o: &Self, t: f64) -> Self {
        let l = |x: f64, y: f64| x + (y - x) * t;
        Self {
            a: l(self.a, o.a),
            b: l(self.b, o.b),
            c: l(self.c, o.c),
            d: l(self.d, o.d),
            tx: l(self.tx, o.tx),
            ty: l(self.ty, o.ty),
        }
    }
}

/// A colour transform: `c' = c * mul + add` per channel (RGBA, 0..1).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ColourTransform {
    pub mul: [f64; 4],
    pub add: [f64; 4],
}

impl ColourTransform {
    pub const IDENTITY: Self = Self {
        mul: [1.0; 4],
        add: [0.0; 4],
    };

    fn from_swf(c: &swf::ColorTransform) -> Self {
        Self {
            mul: [c.r_multiply, c.g_multiply, c.b_multiply, c.a_multiply].map(swf::Fixed8::to_f64),
            add: [c.r_add, c.g_add, c.b_add, c.a_add].map(|a| f64::from(a) / 255.0),
        }
    }

    /// `self` after `inner`.
    #[must_use]
    pub fn then(&self, inner: &Self) -> Self {
        let mut r = *self;
        for i in 0..4 {
            r.mul[i] = self.mul[i] * inner.mul[i];
            r.add[i] = self.mul[i] * inner.add[i] + self.add[i];
        }
        r
    }

    fn apply(&self, c: [f64; 4]) -> [f64; 4] {
        let mut o = [0.0; 4];
        for i in 0..4 {
            o[i] = (c[i] * self.mul[i] + self.add[i]).clamp(0.0, 1.0);
        }
        o
    }
}

/// An RGBA8 image, rows top first, straight (not premultiplied) alpha.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Image {
    pub width: u32,
    pub height: u32,
    pub rgba: Vec<u8>,
}

#[derive(Debug, Clone, PartialEq)]
enum Fill {
    Solid([f64; 4]),
    /// Gradient space to shape space, spread (0 pad, 1 reflect, 2 repeat),
    /// stops (ratio 0..1, colour), radial.
    Gradient {
        matrix: Matrix,
        spread: u8,
        stops: Vec<(f64, [f64; 4])>,
        radial: bool,
    },
    Bitmap {
        id: u16,
        matrix: Matrix,
        repeat: bool,
    },
}

fn colour(c: swf::Color) -> [f64; 4] {
    [c.r, c.g, c.b, c.a].map(|v| f64::from(v) / 255.0)
}

fn fill_of(f: &swf::FillStyle) -> Fill {
    let gradient = |g: &swf::Gradient, radial| Fill::Gradient {
        matrix: Matrix::from_swf(&g.matrix),
        spread: g.spread as u8,
        stops: g
            .records
            .iter()
            .map(|r| (f64::from(r.ratio) / 255.0, colour(r.color)))
            .collect(),
        radial,
    };
    match f {
        swf::FillStyle::Color(c) => Fill::Solid(colour(*c)),
        swf::FillStyle::LinearGradient(g) => gradient(g, false),
        swf::FillStyle::RadialGradient(g) | swf::FillStyle::FocalGradient { gradient: g, .. } => {
            gradient(g, true)
        }
        swf::FillStyle::Bitmap {
            id,
            matrix,
            is_repeating,
            ..
        } => Fill::Bitmap {
            id: *id,
            matrix: Matrix::from_swf(matrix),
            repeat: *is_repeating,
        },
    }
}

fn lerp_fill(a: &Fill, b: &Fill, t: f64) -> Fill {
    let lc = |x: [f64; 4], y: [f64; 4]| {
        let mut o = [0.0; 4];
        for i in 0..4 {
            o[i] = x[i] + (y[i] - x[i]) * t;
        }
        o
    };
    match (a, b) {
        (Fill::Solid(x), Fill::Solid(y)) => Fill::Solid(lc(*x, *y)),
        (
            Fill::Gradient {
                matrix: ma,
                spread,
                stops: sa,
                radial,
            },
            Fill::Gradient {
                matrix: mb,
                stops: sb,
                ..
            },
        ) if sa.len() == sb.len() => Fill::Gradient {
            matrix: ma.lerp(mb, t),
            spread: *spread,
            stops: sa
                .iter()
                .zip(sb)
                .map(|(x, y)| (x.0 + (y.0 - x.0) * t, lc(x.1, y.1)))
                .collect(),
            radial: *radial,
        },
        _ => a.clone(),
    }
}

/// An edge in shape space (twips): a line or a quadratic curve, with its
/// fill styles (left 0, right 1; 0 for none, else 1-based into the
/// shape's style lists) and line style.
#[derive(Debug, Clone, Copy, PartialEq)]
struct Edge {
    from: (f64, f64),
    control: Option<(f64, f64)>,
    to: (f64, f64),
    fill0: u32,
    fill1: u32,
    line: u32,
}

#[derive(Debug, Clone, PartialEq)]
struct ShapeDef {
    fills: Vec<Fill>,
    /// Width (twips), fill.
    lines: Vec<(f64, Fill)>,
    edges: Vec<Edge>,
}

/// A morph shape: start and end styles and edges, one to one.
#[derive(Debug, Clone, PartialEq)]
struct MorphDef {
    start: ShapeDef,
    end: ShapeDef,
}

impl MorphDef {
    fn at(&self, t: f64) -> ShapeDef {
        let lp = |a: (f64, f64), b: (f64, f64)| (a.0 + (b.0 - a.0) * t, a.1 + (b.1 - a.1) * t);
        let mid = |e: &Edge| {
            e.control
                .unwrap_or((e.from.0.midpoint(e.to.0), e.from.1.midpoint(e.to.1)))
        };
        let edges = self
            .start
            .edges
            .iter()
            .zip(&self.end.edges)
            .map(|(a, b)| Edge {
                from: lp(a.from, b.from),
                control: if a.control.is_none() && b.control.is_none() {
                    None
                } else {
                    Some(lp(mid(a), mid(b)))
                },
                to: lp(a.to, b.to),
                ..*a
            })
            .collect();
        ShapeDef {
            fills: self
                .start
                .fills
                .iter()
                .zip(&self.end.fills)
                .map(|(a, b)| lerp_fill(a, b, t))
                .collect(),
            lines: self
                .start
                .lines
                .iter()
                .zip(&self.end.lines)
                .map(|(a, b)| (a.0 + (b.0 - a.0) * t, lerp_fill(&a.1, &b.1, t)))
                .collect(),
            edges,
        }
    }
}

/// The edges of shape records, with styles as they change (a record's new
/// styles appended to the lists, later indices offset past the earlier
/// ones).
fn edges_of(
    records: &[swf::ShapeRecord],
    fills: &mut Vec<Fill>,
    lines: &mut Vec<(f64, Fill)>,
) -> Vec<Edge> {
    let mut out = Vec::new();
    let (mut x, mut y) = (0.0, 0.0);
    let (mut f0, mut f1, mut ln) = (0u32, 0u32, 0u32);
    let (mut fill_base, mut line_base) = (0u32, 0u32);
    for r in records {
        match r {
            swf::ShapeRecord::StyleChange(s) => {
                if let Some(n) = &s.new_styles {
                    fill_base = u32::try_from(fills.len()).unwrap_or(0);
                    line_base = u32::try_from(lines.len()).unwrap_or(0);
                    fills.extend(n.fill_styles.iter().map(fill_of));
                    lines.extend(
                        n.line_styles
                            .iter()
                            .map(|l| (f64::from(l.width().get()), fill_of(l.fill_style()))),
                    );
                    (f0, f1, ln) = (0, 0, 0);
                }
                if let Some(p) = &s.move_to {
                    (x, y) = (f64::from(p.x.get()), f64::from(p.y.get()));
                }
                let based = |v: u32, base: u32| if v == 0 { 0 } else { v + base };
                if let Some(v) = s.fill_style_0 {
                    f0 = based(v, fill_base);
                }
                if let Some(v) = s.fill_style_1 {
                    f1 = based(v, fill_base);
                }
                if let Some(v) = s.line_style {
                    ln = based(v, line_base);
                }
            }
            swf::ShapeRecord::StraightEdge { delta } => {
                let to = (x + f64::from(delta.dx.get()), y + f64::from(delta.dy.get()));
                out.push(Edge {
                    from: (x, y),
                    control: None,
                    to,
                    fill0: f0,
                    fill1: f1,
                    line: ln,
                });
                (x, y) = to;
            }
            swf::ShapeRecord::CurvedEdge {
                control_delta,
                anchor_delta,
            } => {
                let c = (
                    x + f64::from(control_delta.dx.get()),
                    y + f64::from(control_delta.dy.get()),
                );
                let to = (
                    c.0 + f64::from(anchor_delta.dx.get()),
                    c.1 + f64::from(anchor_delta.dy.get()),
                );
                out.push(Edge {
                    from: (x, y),
                    control: Some(c),
                    to,
                    fill0: f0,
                    fill1: f1,
                    line: ln,
                });
                (x, y) = to;
            }
        }
    }
    out
}

/// The end shape's edges, positioned by the start's: the end records hold
/// only moves and edges, in step with the start's.
fn morph_end_edges(
    start: &[swf::ShapeRecord],
    end: &[swf::ShapeRecord],
    like: &[Edge],
) -> Vec<Edge> {
    let mut out = Vec::with_capacity(like.len());
    let mut it = end.iter();
    let (mut x, mut y) = (0.0, 0.0);
    let mut k = 0;
    for r in start {
        if let swf::ShapeRecord::StyleChange(s) = r {
            if s.move_to.is_some() {
                // the end's matching move
                for e in it.by_ref() {
                    if let swf::ShapeRecord::StyleChange(es) = e {
                        if let Some(p) = &es.move_to {
                            (x, y) = (f64::from(p.x.get()), f64::from(p.y.get()));
                        }
                        break;
                    }
                }
            }
        } else {
            {
                let Some(proto) = like.get(k) else { break };
                k += 1;
                let mut edge = None;
                for e in it.by_ref() {
                    match e {
                        swf::ShapeRecord::StyleChange(es) => {
                            if let Some(p) = &es.move_to {
                                (x, y) = (f64::from(p.x.get()), f64::from(p.y.get()));
                            }
                        }
                        swf::ShapeRecord::StraightEdge { delta } => {
                            let to = (x + f64::from(delta.dx.get()), y + f64::from(delta.dy.get()));
                            edge = Some(Edge {
                                from: (x, y),
                                control: None,
                                to,
                                ..*proto
                            });
                            (x, y) = to;
                            break;
                        }
                        swf::ShapeRecord::CurvedEdge {
                            control_delta,
                            anchor_delta,
                        } => {
                            let c = (
                                x + f64::from(control_delta.dx.get()),
                                y + f64::from(control_delta.dy.get()),
                            );
                            let to = (
                                c.0 + f64::from(anchor_delta.dx.get()),
                                c.1 + f64::from(anchor_delta.dy.get()),
                            );
                            edge = Some(Edge {
                                from: (x, y),
                                control: Some(c),
                                to,
                                ..*proto
                            });
                            (x, y) = to;
                            break;
                        }
                    }
                }
                out.push(edge.unwrap_or(*proto));
            }
        }
    }
    out
}

/// One frame's display-list operation.
#[derive(Debug, Clone, PartialEq)]
enum Op {
    Place {
        depth: u16,
        /// `Some` for a new or replaced character.
        id: Option<u16>,
        is_move: bool,
        matrix: Option<Matrix>,
        cx: Option<ColourTransform>,
        ratio: Option<u16>,
        name: Option<String>,
    },
    Remove {
        depth: u16,
    },
}

#[derive(Debug, Clone, PartialEq)]
struct SpriteDef {
    /// The operations of each frame (index 0: frame 1).
    frames: Vec<Vec<Op>>,
    /// Frame labels (1-based frames).
    labels: HashMap<String, u32>,
}

fn sprite_of(tags: &[swf::Tag], count: u16) -> SpriteDef {
    let mut frames = vec![Vec::new()];
    let mut labels = HashMap::new();
    for t in tags {
        match t {
            swf::Tag::ShowFrame => frames.push(Vec::new()),
            swf::Tag::FrameLabel(l) => {
                labels.insert(
                    l.label.to_string_lossy(swf::UTF_8),
                    u32::try_from(frames.len()).unwrap_or(1),
                );
            }
            swf::Tag::PlaceObject(p) => {
                let (id, is_move) = match p.action {
                    swf::PlaceObjectAction::Place(id) => (Some(id), false),
                    swf::PlaceObjectAction::Replace(id) => (Some(id), true),
                    swf::PlaceObjectAction::Modify => (None, true),
                };
                if let Some(f) = frames.last_mut() {
                    f.push(Op::Place {
                        depth: p.depth,
                        id,
                        is_move,
                        matrix: p.matrix.as_ref().map(Matrix::from_swf),
                        cx: p.color_transform.as_ref().map(ColourTransform::from_swf),
                        ratio: p.ratio,
                        name: p.name.map(|n| n.to_string_lossy(swf::UTF_8)),
                    });
                }
            }
            swf::Tag::RemoveObject(r) => {
                if let Some(f) = frames.last_mut() {
                    f.push(Op::Remove { depth: r.depth });
                }
            }
            _ => {}
        }
    }
    // the ops after the last ShowFrame belong to no shown frame
    frames.truncate(usize::from(count.max(1)));
    while frames.len() < usize::from(count.max(1)) {
        frames.push(Vec::new());
    }
    SpriteDef { frames, labels }
}

#[derive(Debug, Clone, PartialEq)]
enum Character {
    Shape(ShapeDef),
    Morph(MorphDef),
    Sprite(SpriteDef),
    Bitmap(Image),
}

/// An instance on a display list.
#[derive(Debug, Clone, PartialEq)]
pub struct Instance {
    pub depth: u16,
    pub id: u16,
    pub matrix: Matrix,
    pub cx: ColourTransform,
    pub ratio: u16,
    pub name: Option<String>,
    /// The frame it was placed at (1-based).
    pub placed: u32,
}

/// A parsed movie.
#[derive(Debug, Clone, PartialEq)]
pub struct Movie {
    /// Stage size (pixels) and frame rate.
    pub width: f64,
    pub height: f64,
    pub fps: f64,
    chars: HashMap<u16, Character>,
    /// The main timeline (character 0).
    root: SpriteDef,
    /// `SymbolClass` names by character.
    pub classes: HashMap<u16, String>,
}

/// Why a movie could not be read.
#[derive(Debug)]
pub struct Error(pub String);

impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for Error {}

/// A `DefineBitsLossless` bitmap as RGBA (straight alpha).
fn bitmap(b: &swf::DefineBitsLossless) -> Option<Image> {
    let (w, h) = (u32::from(b.width), u32::from(b.height));
    let mut z = Vec::new();
    flate2::read::ZlibDecoder::new(&b.data[..])
        .read_to_end(&mut z)
        .ok()?;
    let alpha = b.version == 2;
    let mut rgba = Vec::with_capacity((w * h * 4) as usize);
    match b.format {
        swf::BitmapFormat::ColorMap8 { num_colors } => {
            let n = usize::from(num_colors) + 1;
            let ps = if alpha { 4 } else { 3 };
            let (pal, px) = z.split_at_checked(n * ps)?;
            let stride = (w as usize).div_ceil(4) * 4;
            for y in 0..h as usize {
                for x in 0..w as usize {
                    let i = usize::from(*px.get(y * stride + x)?);
                    let c = pal.get(i * ps..i * ps + ps).unwrap_or(&[0, 0, 0, 0]);
                    if alpha {
                        rgba.extend_from_slice(&unpremultiply([c[0], c[1], c[2], c[3]]));
                    } else {
                        rgba.extend_from_slice(&[c[0], c[1], c[2], 255]);
                    }
                }
            }
        }
        swf::BitmapFormat::Rgb32 => {
            for p in z.as_chunks::<4>().0.iter().take((w * h) as usize) {
                let a = if alpha { p[0] } else { 255 };
                rgba.extend_from_slice(&unpremultiply([p[1], p[2], p[3], a]));
            }
        }
        swf::BitmapFormat::Rgb15 => {
            let stride = (w as usize * 2).div_ceil(4) * 4;
            for y in 0..h as usize {
                for x in 0..w as usize {
                    let at = y * stride + 2 * x;
                    let v = u16::from_be_bytes([*z.get(at)?, *z.get(at + 1)?]);
                    #[allow(clippy::cast_possible_truncation)] // 5 bits
                    let ch = |s: u16| ((u32::from((v >> s) & 0x1f) * 255 + 15) / 31) as u8;
                    rgba.extend_from_slice(&[ch(10), ch(5), ch(0), 255]);
                }
            }
        }
    }
    (rgba.len() == (w * h * 4) as usize).then_some(Image {
        width: w,
        height: h,
        rgba,
    })
}

/// A JPEG bitmap (`DefineBits` with the movie's `JPEGTables`, `DefineBitsJPEG2`,
/// `DefineBitsJPEG3` with its zlib alpha plane) as RGBA.
fn jpeg(tables: Option<&[u8]>, data: &[u8], alpha: Option<&[u8]>) -> Option<Image> {
    // an erroneous leading EOI+SOI (FF D9 FF D8) some writers put first
    let data = if data.starts_with(&[0xff, 0xd9, 0xff, 0xd8]) {
        &data[2..]
    } else {
        data
    };
    let mut bytes = Vec::with_capacity(data.len() + tables.map_or(0, <[u8]>::len));
    match tables {
        // the tables' stream (SOI .. EOI) then the image's without its SOI
        Some(t) if t.len() > 4 => {
            let t = t.strip_suffix(&[0xff, 0xd9][..]).unwrap_or(t);
            bytes.extend_from_slice(t);
            bytes.extend_from_slice(data.strip_prefix(&[0xff, 0xd8][..]).unwrap_or(data));
        }
        _ => bytes.extend_from_slice(data),
    }
    let mut d = jpeg_decoder::Decoder::new(&bytes[..]);
    let px = d.decode().ok()?;
    let info = d.info()?;
    let (w, h) = (u32::from(info.width), u32::from(info.height));
    let n = (w * h) as usize;
    let mut rgba = Vec::with_capacity(n * 4);
    match info.pixel_format {
        jpeg_decoder::PixelFormat::RGB24 => {
            for p in px.as_chunks::<3>().0.iter().take(n) {
                rgba.extend_from_slice(&[p[0], p[1], p[2], 255]);
            }
        }
        jpeg_decoder::PixelFormat::L8 => {
            for &l in px.iter().take(n) {
                rgba.extend_from_slice(&[l, l, l, 255]);
            }
        }
        _ => return None,
    }
    if let Some(a) = alpha.filter(|a| !a.is_empty()) {
        let mut z = Vec::new();
        if flate2::read::ZlibDecoder::new(a)
            .read_to_end(&mut z)
            .is_ok()
            && z.len() >= n
        {
            for (i, &al) in z.iter().take(n).enumerate() {
                // the colour planes are premultiplied by the alpha
                let p = &mut rgba[i * 4..i * 4 + 4];
                let u = unpremultiply([p[0], p[1], p[2], al]);
                p.copy_from_slice(&u);
            }
        }
    }
    (rgba.len() == n * 4).then_some(Image {
        width: w,
        height: h,
        rgba,
    })
}

/// SWF's lossless alpha bitmaps are premultiplied.
fn unpremultiply(p: [u8; 4]) -> [u8; 4] {
    let a = p[3];
    if a == 0 || a == 255 {
        return if a == 0 { [0, 0, 0, 0] } else { p };
    }
    #[allow(clippy::cast_possible_truncation)] // at most 255
    let u = |c: u8| ((u32::from(c) * 255 + u32::from(a) / 2) / u32::from(a)).min(255) as u8;
    [u(p[0]), u(p[1]), u(p[2]), a]
}

impl Movie {
    /// Parses a `.swf` file (compressed or not).
    ///
    /// # Errors
    /// When it is not a movie the reader takes.
    pub fn parse(data: &[u8]) -> Result<Self, Error> {
        let buf = swf::decompress_swf(data).map_err(|e| Error(format!("swf: {e}")))?;
        let parsed = swf::parse_swf(&buf).map_err(|e| Error(format!("swf: {e}")))?;
        let header = &parsed.header;
        let stage = header.stage_size();
        let mut chars = HashMap::new();
        let mut classes = HashMap::new();
        let mut tables: Option<&[u8]> = None;
        for t in &parsed.tags {
            match t {
                swf::Tag::JpegTables(j) => tables = Some(j),
                swf::Tag::DefineBits { id, jpeg_data } => {
                    if let Some(img) = jpeg(tables, jpeg_data, None) {
                        chars.insert(*id, Character::Bitmap(img));
                    }
                }
                swf::Tag::DefineBitsJpeg2 { id, jpeg_data } => {
                    if let Some(img) = jpeg(None, jpeg_data, None) {
                        chars.insert(*id, Character::Bitmap(img));
                    }
                }
                swf::Tag::DefineBitsJpeg3(j) => {
                    if let Some(img) = jpeg(None, j.data, Some(j.alpha_data)) {
                        chars.insert(j.id, Character::Bitmap(img));
                    }
                }
                swf::Tag::DefineShape(s) => {
                    let mut fills: Vec<Fill> = s.styles.fill_styles.iter().map(fill_of).collect();
                    let mut lines: Vec<(f64, Fill)> = s
                        .styles
                        .line_styles
                        .iter()
                        .map(|l| (f64::from(l.width().get()), fill_of(l.fill_style())))
                        .collect();
                    let edges = edges_of(&s.shape, &mut fills, &mut lines);
                    chars.insert(
                        s.id,
                        Character::Shape(ShapeDef {
                            fills,
                            lines,
                            edges,
                        }),
                    );
                }
                swf::Tag::DefineMorphShape(m) => {
                    let side = |ms: &swf::MorphShape| {
                        (
                            ms.fill_styles.iter().map(fill_of).collect::<Vec<_>>(),
                            ms.line_styles
                                .iter()
                                .map(|l| (f64::from(l.width().get()), fill_of(l.fill_style())))
                                .collect::<Vec<_>>(),
                        )
                    };
                    let (mut fa, mut la) = side(&m.start);
                    let (fb, lb) = side(&m.end);
                    let edges = edges_of(&m.start.shape, &mut fa, &mut la);
                    let end_edges = morph_end_edges(&m.start.shape, &m.end.shape, &edges);
                    chars.insert(
                        m.id,
                        Character::Morph(MorphDef {
                            start: ShapeDef {
                                fills: fa,
                                lines: la,
                                edges,
                            },
                            end: ShapeDef {
                                fills: fb,
                                lines: lb,
                                edges: end_edges,
                            },
                        }),
                    );
                }
                swf::Tag::DefineSprite(s) => {
                    chars.insert(s.id, Character::Sprite(sprite_of(&s.tags, s.num_frames)));
                }
                swf::Tag::DefineBitsLossless(b) => {
                    if let Some(img) = bitmap(b) {
                        chars.insert(b.id, Character::Bitmap(img));
                    }
                }
                swf::Tag::SymbolClass(links) => {
                    for l in links {
                        classes.insert(l.id, l.class_name.to_string_lossy(swf::UTF_8));
                    }
                }
                _ => {}
            }
        }
        Ok(Self {
            width: stage.width().to_pixels(),
            height: stage.height().to_pixels(),
            fps: f64::from(header.frame_rate().to_f32()),
            chars,
            root: sprite_of(&parsed.tags, header.num_frames()),
            classes,
        })
    }

    /// Frames of character `id` (0: the main timeline); 1 for a shape.
    #[must_use]
    pub fn frames(&self, id: u16) -> u32 {
        match self.sprite(id) {
            Some(s) => u32::try_from(s.frames.len()).unwrap_or(1),
            None => 1,
        }
    }

    fn sprite(&self, id: u16) -> Option<&SpriteDef> {
        if id == 0 {
            return Some(&self.root);
        }
        match self.chars.get(&id) {
            Some(Character::Sprite(s)) => Some(s),
            _ => None,
        }
    }

    /// Bitmap `id` (a `DefineBitsLossless`), if the movie has it.
    #[must_use]
    pub fn bitmap(&self, id: u16) -> Option<&Image> {
        match self.chars.get(&id) {
            Some(Character::Bitmap(b)) => Some(b),
            _ => None,
        }
    }

    /// The character exported as class `name` (`SymbolClass`).
    #[must_use]
    pub fn class(&self, name: &str) -> Option<u16> {
        self.classes
            .iter()
            .find(|(_, n)| n.as_str() == name)
            .map(|(&id, _)| id)
    }

    /// The frame (1-based) of label `label` in sprite `id` (0: the main
    /// timeline).
    #[must_use]
    pub fn label(&self, id: u16, label: &str) -> Option<u32> {
        self.sprite(id)?.labels.get(label).copied()
    }

    /// The bounds of character `id` at `frame` through `m` (its space in
    /// pixels, as [`Movie::render`] takes it): `(x0, y0, x1, y1)`, `None`
    /// when it draws nothing.
    #[must_use]
    pub fn bounds(&self, id: u16, frame: u32, m: &Matrix) -> Option<(f64, f64, f64, f64)> {
        let mut b = None;
        let twips = m.then(&Matrix::scale(1.0 / 20.0));
        self.bounds_into(id, frame, 0, &twips, &mut b, 0);
        b
    }

    fn bounds_into(
        &self,
        id: u16,
        frame: u32,
        ratio: u16,
        m: &Matrix,
        b: &mut Option<(f64, f64, f64, f64)>,
        nesting: u32,
    ) {
        if nesting > 16 {
            return;
        }
        let mut add = |p: (f64, f64)| {
            let q = m.apply(p);
            *b = Some(match *b {
                None => (q.0, q.1, q.0, q.1),
                Some((x0, y0, x1, y1)) => (x0.min(q.0), y0.min(q.1), x1.max(q.0), y1.max(q.1)),
            });
        };
        let shape = match self.chars.get(&id) {
            Some(Character::Shape(s)) => Some(s.clone()),
            Some(Character::Morph(mo)) => Some(mo.at(f64::from(ratio) / 65535.0)),
            _ => None,
        };
        if let Some(s) = shape {
            for e in &s.edges {
                if e.fill0 == 0 && e.fill1 == 0 && e.line == 0 {
                    continue;
                }
                add(e.from);
                add(e.to);
                if let Some(c) = e.control {
                    add(c);
                }
            }
            return;
        }
        let Some(s) = self.sprite(id) else { return };
        let n = u32::try_from(s.frames.len()).unwrap_or(1).max(1);
        let own = (frame.max(1) - 1) % n + 1;
        for i in list_of(s, own) {
            let child = own.saturating_sub(i.placed) + 1;
            self.bounds_into(i.id, child, i.ratio, &m.then(&i.matrix), b, nesting + 1);
        }
    }

    /// The bitmaps' ids, in order.
    #[must_use]
    pub fn bitmap_ids(&self) -> Vec<u16> {
        let mut v: Vec<u16> = self
            .chars
            .iter()
            .filter(|(_, c)| matches!(c, Character::Bitmap(_)))
            .map(|(&k, _)| k)
            .collect();
        v.sort_unstable();
        v
    }

    /// The display list of sprite `id` (0: the main timeline) at `frame`
    /// (1-based; past the end, the last).
    #[must_use]
    pub fn display_list(&self, id: u16, frame: u32) -> Vec<Instance> {
        let Some(s) = self.sprite(id) else {
            return Vec::new();
        };
        let mut list: BTreeMap<u16, Instance> = BTreeMap::new();
        let last = frame.clamp(1, u32::try_from(s.frames.len()).unwrap_or(1));
        for (n, ops) in (1u32..).zip(&s.frames) {
            if n > last {
                break;
            }
            for op in ops {
                match op {
                    Op::Remove { depth } => {
                        list.remove(depth);
                    }
                    Op::Place {
                        depth,
                        id,
                        is_move,
                        matrix,
                        cx,
                        ratio,
                        name,
                    } => {
                        if *is_move && let Some(i) = list.get_mut(depth) {
                            if let Some(new) = id
                                && *new != i.id
                            {
                                i.id = *new;
                                i.placed = n;
                            }
                            if let Some(m) = matrix {
                                i.matrix = *m;
                            }
                            if let Some(c) = cx {
                                i.cx = *c;
                            }
                            if let Some(r) = ratio {
                                i.ratio = *r;
                            }
                            if name.is_some() {
                                i.name.clone_from(name);
                            }
                            continue;
                        }
                        let Some(id) = id else { continue };
                        list.insert(
                            *depth,
                            Instance {
                                depth: *depth,
                                id: *id,
                                matrix: matrix.unwrap_or(Matrix::IDENTITY),
                                cx: cx.unwrap_or(ColourTransform::IDENTITY),
                                ratio: ratio.unwrap_or(0),
                                name: name.clone(),
                                placed: n,
                            },
                        );
                    }
                }
            }
        }
        list.into_values().collect()
    }

    /// Character `id` at `frame` (sprites; 1-based, looping) drawn into a
    /// `width` x `height` image through `to_pixels` (its space in pixels,
    /// not twips: the twips are divided by 20 first), with colour transform
    /// `cx`.
    #[must_use]
    pub fn render(
        &self,
        id: u16,
        frame: u32,
        width: u32,
        height: u32,
        to_pixels: &Matrix,
        cx: &ColourTransform,
    ) -> Image {
        let mut canvas = Canvas::new(width, height);
        let twips = to_pixels.then(&Matrix::scale(1.0 / 20.0));
        self.draw(&mut canvas, id, frame, 0, &twips, cx, 0);
        canvas.into_image()
    }

    #[allow(clippy::too_many_arguments)]
    fn draw(
        &self,
        canvas: &mut Canvas,
        id: u16,
        frame: u32,
        ratio: u16,
        m: &Matrix,
        cx: &ColourTransform,
        nesting: u32,
    ) {
        if nesting > 16 {
            return;
        }
        if id == 0 {
            self.draw_sprite(canvas, &self.root, frame, m, cx, nesting);
            return;
        }
        match self.chars.get(&id) {
            Some(Character::Shape(s)) => canvas.shape(s, m, cx, self),
            Some(Character::Morph(mo)) => {
                canvas.shape(&mo.at(f64::from(ratio) / 65535.0), m, cx, self);
            }
            Some(Character::Sprite(s)) => self.draw_sprite(canvas, s, frame, m, cx, nesting),
            Some(Character::Bitmap(_)) | None => {}
        }
    }

    fn draw_sprite(
        &self,
        canvas: &mut Canvas,
        s: &SpriteDef,
        frame: u32,
        m: &Matrix,
        cx: &ColourTransform,
        nesting: u32,
    ) {
        let n = u32::try_from(s.frames.len()).unwrap_or(1).max(1);
        let own = (frame.max(1) - 1) % n + 1;
        // the display list of this sprite at its own frame
        let list = list_of(s, own);
        for i in list {
            let child_frame = own.saturating_sub(i.placed) + 1;
            let mm = m.then(&i.matrix);
            let cc = cx.then(&i.cx);
            self.draw(canvas, i.id, child_frame, i.ratio, &mm, &cc, nesting + 1);
        }
    }
}

/// A sprite's display list by its definition (as [`Movie::display_list`]).
fn list_of(s: &SpriteDef, frame: u32) -> Vec<Instance> {
    let tmp = Movie {
        width: 0.0,
        height: 0.0,
        fps: 0.0,
        chars: HashMap::new(),
        root: s.clone(),
        classes: HashMap::new(),
    };
    tmp.display_list(0, frame)
}

/// A line segment in pixels.
type Seg = ((f64, f64), (f64, f64));

/// Subsamples per pixel row (vertical) for the coverage.
const SUB: usize = 5;

/// A premultiplied RGBA float canvas.
struct Canvas {
    w: usize,
    h: usize,
    px: Vec<[f64; 4]>,
}

impl Canvas {
    fn new(w: u32, h: u32) -> Self {
        let (w, h) = (w as usize, h as usize);
        Self {
            w,
            h,
            px: vec![[0.0; 4]; w * h],
        }
    }

    fn into_image(self) -> Image {
        let mut rgba = Vec::with_capacity(self.px.len() * 4);
        for p in &self.px {
            let a = p[3].clamp(0.0, 1.0);
            #[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)] // clamped
            let q = |v: f64| (v.clamp(0.0, 1.0) * 255.0).round() as u8;
            if a <= 0.0 {
                rgba.extend_from_slice(&[0, 0, 0, 0]);
            } else {
                rgba.extend_from_slice(&[q(p[0] / a), q(p[1] / a), q(p[2] / a), q(a)]);
            }
        }
        Image {
            width: u32::try_from(self.w).unwrap_or(0),
            height: u32::try_from(self.h).unwrap_or(0),
            rgba,
        }
    }

    /// A shape: each fill style's region (even-odd over its edges), then
    /// each line style's strokes.
    fn shape(&mut self, s: &ShapeDef, m: &Matrix, cx: &ColourTransform, movie: &Movie) {
        let Some(inv) = m.inverse() else { return };
        for (k, fill) in (1u32..).zip(&s.fills) {
            let mut segs = Vec::new();
            for e in &s.edges {
                if e.fill0 == k {
                    flatten(e, m, &mut segs);
                }
                if e.fill1 == k {
                    flatten(e, m, &mut segs);
                }
            }
            if !segs.is_empty() {
                self.fill(&segs, false, fill, &inv, cx, movie);
            }
        }
        // the stroke width scales with the matrix (its mean scale)
        let scale = ((m.a * m.d - m.b * m.c).abs()).sqrt();
        for (k, (width, fill)) in (1u32..).zip(&s.lines) {
            let half = (width * scale / 2.0).max(0.5);
            let mut segs = Vec::new();
            for e in &s.edges {
                if e.line != k {
                    continue;
                }
                let mut pts = Vec::new();
                flatten(e, m, &mut pts);
                for &(a, b) in &pts {
                    stroke_segment(a, b, half, &mut segs);
                }
            }
            if !segs.is_empty() {
                self.fill(&segs, true, fill, &inv, cx, movie);
            }
        }
    }

    /// Fills the region the segments (pixel space) bound: even-odd, or
    /// non-zero winding; `inv` takes a pixel back to shape space for the
    /// fill's colour.
    fn fill(
        &mut self,
        segs: &[Seg],
        nonzero: bool,
        fill: &Fill,
        inv: &Matrix,
        cx: &ColourTransform,
        movie: &Movie,
    ) {
        let (mut y0, mut y1) = (f64::MAX, f64::MIN);
        for (a, b) in segs {
            y0 = y0.min(a.1).min(b.1);
            y1 = y1.max(a.1).max(b.1);
        }
        #[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)] // clamped
        let (row0, row1) = (
            y0.floor().max(0.0) as usize,
            (y1.ceil().max(0.0) as usize).min(self.h),
        );
        let mut cover = vec![0.0f64; self.w + 2];
        let mut xs: Vec<(f64, i32)> = Vec::new();
        for row in row0..row1 {
            cover.fill(0.0);
            let mut any = false;
            for sub in 0..SUB {
                #[allow(clippy::cast_precision_loss)] // small
                let sy = row as f64 + (sub as f64 + 0.5) / SUB as f64;
                xs.clear();
                for &(a, b) in segs {
                    if (a.1 <= sy) == (b.1 <= sy) {
                        continue;
                    }
                    let t = (sy - a.1) / (b.1 - a.1);
                    xs.push((a.0 + (b.0 - a.0) * t, if b.1 > a.1 { 1 } else { -1 }));
                }
                if xs.len() < 2 {
                    continue;
                }
                xs.sort_by(|p, q| p.0.total_cmp(&q.0));
                let mut wind = 0i32;
                for k in 0..xs.len() - 1 {
                    wind = if nonzero { wind + xs[k].1 } else { wind ^ 1 };
                    if wind == 0 {
                        continue;
                    }
                    let (a, b) = (xs[k].0, xs[k + 1].0);
                    #[allow(clippy::cast_precision_loss)] // small
                    span(&mut cover, a, b, 1.0 / SUB as f64, self.w);
                    any = true;
                }
            }
            if !any {
                continue;
            }
            for (x, &c) in cover.iter().enumerate().take(self.w) {
                if c <= 1e-6 {
                    continue;
                }
                #[allow(clippy::cast_precision_loss)] // small
                let p = inv.apply((x as f64 + 0.5, row as f64 + 0.5));
                let col = cx.apply(sample(fill, p, movie));
                let a = col[3] * c.min(1.0);
                let dst = &mut self.px[row * self.w + x];
                for i in 0..3 {
                    dst[i] = col[i] * a + dst[i] * (1.0 - a);
                }
                dst[3] = a + dst[3] * (1.0 - a);
            }
        }
    }
}

/// Adds coverage `weight` over `[a, b)` (pixels) to `cover`, partial at
/// the ends.
fn span(cover: &mut [f64], a: f64, b: f64, weight: f64, w: usize) {
    #[allow(clippy::cast_precision_loss)] // small
    let (a, b) = (a.max(0.0), b.min(w as f64));
    if b <= a {
        return;
    }
    #[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)] // clamped
    let (ia, ib) = (a.floor() as usize, b.floor() as usize);
    if ia == ib {
        cover[ia] += (b - a) * weight;
        return;
    }
    #[allow(clippy::cast_precision_loss)] // small
    {
        cover[ia] += ((ia + 1) as f64 - a) * weight;
        for c in &mut cover[ia + 1..ib] {
            *c += weight;
        }
        if ib < cover.len() {
            cover[ib] += (b - ib as f64) * weight;
        }
    }
}

/// An edge through `m` as line segments (a curve in steps of about two
/// pixels).
fn flatten(e: &Edge, m: &Matrix, out: &mut Vec<Seg>) {
    let a = m.apply(e.from);
    let b = m.apply(e.to);
    let Some(c) = e.control else {
        out.push((a, b));
        return;
    };
    let c = m.apply(c);
    let len = (c.0 - a.0).hypot(c.1 - a.1) + (b.0 - c.0).hypot(b.1 - c.1);
    #[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)] // clamped
    let n = ((len / 2.0).ceil() as usize).clamp(1, 64);
    let mut prev = a;
    for i in 1..=n {
        #[allow(clippy::cast_precision_loss)] // small
        let t = i as f64 / n as f64;
        let u = 1.0 - t;
        let p = (
            u * u * a.0 + 2.0 * u * t * c.0 + t * t * b.0,
            u * u * a.1 + 2.0 * u * t * c.1 + t * t * b.1,
        );
        out.push((prev, p));
        prev = p;
    }
}

/// A stroke segment as a closed quad (counter-clockwise) and a round cap
/// (an octagon) at its end, for a non-zero fill.
fn stroke_segment(a: (f64, f64), b: (f64, f64), half: f64, out: &mut Vec<Seg>) {
    let (dx, dy) = (b.0 - a.0, b.1 - a.1);
    let len = dx.hypot(dy);
    if len > 1e-9 {
        let (nx, ny) = (-dy / len * half, dx / len * half);
        let q = [
            (a.0 + nx, a.1 + ny),
            (b.0 + nx, b.1 + ny),
            (b.0 - nx, b.1 - ny),
            (a.0 - nx, a.1 - ny),
        ];
        // orient every polygon the same way
        let area: f64 = (0..4)
            .map(|i| {
                let (p, r) = (q[i], q[(i + 1) % 4]);
                p.0 * r.1 - r.0 * p.1
            })
            .sum();
        push_polygon(&q, area < 0.0, out);
    }
    let mut oct = [(0.0, 0.0); 8];
    for (i, o) in oct.iter_mut().enumerate() {
        #[allow(clippy::cast_precision_loss)] // small
        let t = std::f64::consts::TAU * i as f64 / 8.0;
        *o = (b.0 + half * t.cos(), b.1 + half * t.sin());
    }
    push_polygon(&oct, false, out);
}

fn push_polygon(p: &[(f64, f64)], reverse: bool, out: &mut Vec<Seg>) {
    let n = p.len();
    for i in 0..n {
        let (a, b) = (p[i], p[(i + 1) % n]);
        out.push(if reverse { (b, a) } else { (a, b) });
    }
}

/// The fill's colour (straight RGBA) at shape-space point `p` (twips).
fn sample(fill: &Fill, p: (f64, f64), movie: &Movie) -> [f64; 4] {
    match fill {
        Fill::Solid(c) => *c,
        Fill::Gradient {
            matrix,
            spread,
            stops,
            radial,
        } => {
            let Some(inv) = matrix.inverse() else {
                return stops.first().map_or([0.0; 4], |s| s.1);
            };
            let g = inv.apply(p);
            let mut t = if *radial {
                g.0.hypot(g.1) / 16384.0
            } else {
                (g.0 + 16384.0) / 32768.0
            };
            t = match spread {
                1 => {
                    let m = t.rem_euclid(2.0);
                    if m > 1.0 { 2.0 - m } else { m }
                }
                2 => t.rem_euclid(1.0),
                _ => t.clamp(0.0, 1.0),
            };
            gradient_at(stops, t)
        }
        Fill::Bitmap { id, matrix, repeat } => {
            let Some(img) = movie.bitmap(*id) else {
                return [0.0; 4];
            };
            let Some(inv) = matrix.inverse() else {
                return [0.0; 4];
            };
            let q = inv.apply(p);
            #[allow(clippy::cast_possible_truncation)] // clamped below
            let (mut x, mut y) = (q.0.floor() as i64, q.1.floor() as i64);
            let (w, h) = (i64::from(img.width), i64::from(img.height));
            if *repeat {
                x = x.rem_euclid(w);
                y = y.rem_euclid(h);
            } else {
                x = x.clamp(0, w - 1);
                y = y.clamp(0, h - 1);
            }
            #[allow(clippy::cast_sign_loss, clippy::cast_possible_truncation)] // in range
            let i = ((y * w + x) * 4) as usize;
            let px = &img.rgba[i..i + 4];
            [px[0], px[1], px[2], px[3]].map(|v| f64::from(v) / 255.0)
        }
    }
}

fn gradient_at(stops: &[(f64, [f64; 4])], t: f64) -> [f64; 4] {
    let Some(first) = stops.first() else {
        return [0.0; 4];
    };
    if t <= first.0 {
        return first.1;
    }
    for w in stops.windows(2) {
        let (a, b) = (&w[0], &w[1]);
        if t <= b.0 {
            let u = if b.0 > a.0 {
                (t - a.0) / (b.0 - a.0)
            } else {
                1.0
            };
            return std::array::from_fn(|i| a.1[i] + (b.1[i] - a.1[i]) * u);
        }
    }
    stops.last().map_or([0.0; 4], |s| s.1)
}

/// The archive path of movie `name` (`chocobo.swf`).
#[must_use]
pub fn movie_path(name: &str) -> String {
    format!(
        "ffx_data/gamedata/ps3data/flash/{}",
        name.to_ascii_lowercase()
    )
}

/// An image as a PNG (8-bit RGBA, no filtering), for looking at.
#[must_use]
pub fn png(img: &Image) -> Vec<u8> {
    use std::io::Write as _;
    fn chunk(out: &mut Vec<u8>, kind: [u8; 4], data: &[u8]) {
        out.extend_from_slice(&u32::try_from(data.len()).unwrap_or(0).to_be_bytes());
        let mut c = flate2::Crc::new();
        c.update(&kind);
        c.update(data);
        out.extend_from_slice(&kind);
        out.extend_from_slice(data);
        out.extend_from_slice(&c.sum().to_be_bytes());
    }
    let mut raw = Vec::with_capacity(img.rgba.len() + img.height as usize);
    for row in img.rgba.chunks_exact((img.width as usize * 4).max(1)) {
        raw.push(0);
        raw.extend_from_slice(row);
    }
    let mut z = flate2::write::ZlibEncoder::new(Vec::new(), flate2::Compression::default());
    let _ = z.write_all(&raw);
    let idat = z.finish().unwrap_or_default();
    let mut out = b"\x89PNG\r\n\x1a\n".to_vec();
    let mut ihdr = Vec::new();
    ihdr.extend_from_slice(&img.width.to_be_bytes());
    ihdr.extend_from_slice(&img.height.to_be_bytes());
    ihdr.extend_from_slice(&[8, 6, 0, 0, 0]);
    chunk(&mut out, *b"IHDR", &ihdr);
    chunk(&mut out, *b"IDAT", &idat);
    chunk(&mut out, *b"IEND", &[]);
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn matrices_compose_and_invert() {
        let m = Matrix {
            a: 2.0,
            b: 0.5,
            c: -1.0,
            d: 3.0,
            tx: 10.0,
            ty: -4.0,
        };
        let p = m.apply((3.0, 7.0));
        let back = m.inverse().expect("invertible").apply(p);
        assert!((back.0 - 3.0).abs() < 1e-9 && (back.1 - 7.0).abs() < 1e-9);
        let s = Matrix::scale(0.5).then(&m);
        let q = s.apply((3.0, 7.0));
        assert!((q.0 - p.0 / 2.0).abs() < 1e-9 && (q.1 - p.1 / 2.0).abs() < 1e-9);
    }

    #[test]
    fn a_square_fills_its_pixels() {
        // a 4x4-pixel square at (2, 2), red
        let e = |a: (f64, f64), b: (f64, f64)| Edge {
            from: a,
            control: None,
            to: b,
            fill0: 0,
            fill1: 1,
            line: 0,
        };
        let s = ShapeDef {
            fills: vec![Fill::Solid([1.0, 0.0, 0.0, 1.0])],
            lines: Vec::new(),
            edges: vec![
                e((40.0, 40.0), (120.0, 40.0)),
                e((120.0, 40.0), (120.0, 120.0)),
                e((120.0, 120.0), (40.0, 120.0)),
                e((40.0, 120.0), (40.0, 40.0)),
            ],
        };
        let movie = Movie {
            width: 8.0,
            height: 8.0,
            fps: 30.0,
            chars: HashMap::from([(1, Character::Shape(s))]),
            root: SpriteDef {
                frames: vec![vec![Op::Place {
                    depth: 1,
                    id: Some(1),
                    is_move: false,
                    matrix: None,
                    cx: Some(ColourTransform {
                        mul: [1.0, 1.0, 1.0, 0.5],
                        add: [0.0; 4],
                    }),
                    ratio: None,
                    name: None,
                }]],
                labels: HashMap::new(),
            },
            classes: HashMap::new(),
        };
        let img = movie.render(0, 1, 8, 8, &Matrix::IDENTITY, &ColourTransform::IDENTITY);
        let at = |x: usize, y: usize| &img.rgba[(y * 8 + x) * 4..(y * 8 + x) * 4 + 4];
        assert_eq!(at(3, 3), &[255, 0, 0, 128]);
        assert_eq!(at(1, 1), &[0, 0, 0, 0]);
        assert_eq!(at(6, 6), &[0, 0, 0, 0]);
        assert_eq!(movie.display_list(0, 1).len(), 1);
    }

    #[test]
    fn gradients_interpolate_and_pad() {
        let stops = vec![(0.0, [0.0, 0.0, 0.0, 1.0]), (1.0, [1.0, 1.0, 1.0, 1.0])];
        assert_eq!(gradient_at(&stops, 0.5), [0.5, 0.5, 0.5, 1.0]);
        assert_eq!(gradient_at(&stops, -1.0), [0.0, 0.0, 0.0, 1.0]);
        assert_eq!(gradient_at(&stops, 2.0), [1.0, 1.0, 1.0, 1.0]);
    }
}
