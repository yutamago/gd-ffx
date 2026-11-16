//! Morphing map meshes (docs/importer.md, "Morphing meshes"): the inputs a
//! map instance's modifier network blends, read from the cluster.
//!
//! A morphing instance's `PMeshInstance.m_dynamicMeshInstance` (`+0x18`)
//! points at a `PModifierNetworkDynamicMeshInstance`, whose `m_dynamicMesh`
//! is a `PModifierNetworkDynamicMesh` over the instance's own `PMesh`:
//!
//! - `m_segments` (`+0x10`): one `PDynamicSegmentDesc` per mesh segment, in
//!   mesh order (`m_elementCount`: the segment's vertex count);
//! - `m_modifierNetworkInputs` (`+0x1c`): the inputs, `PDynamicDataBlock`s
//!   (`m_stride`, `m_elementCount`, one `PVertexStream` naming the render
//!   type, `m_data` the values, in the list's arrays one after another);
//! - `m_segmentInputs` (`+0x34`): one `PModifierNetworkDynamicMeshSegment`
//!   per segment, whose `m_segmentInputGroupStart` is its first input and
//!   whose `PModifierNetwork` (`m_totalInputCount`) says how many it has.
//!
//! Each mesh's segments, inputs and segment inputs follow the previous
//! mesh's in their lists (the pointer fixups give the same starts on
//! `bsil00`). The network's modifier is `Morph23` on every map seen: two
//! inputs of three floats, blended with the weights `1 - w`, `w`
//! (`PMorphModifier_blend2x3` `0x005ca0d0`; `MorphManager_setWeights`
//! `0x006abfc0`), the output written over the segment's `Vertex` stream.

use ffx_phyre::cluster::Cluster;
use ffx_phyre::fixup::PointerFixup;

/// One input of a segment's morph: a full set of the segment's values.
#[derive(Debug, Clone, PartialEq)]
pub struct Input {
    /// The `PVertexStream`'s render type (`Vertex` on every map seen).
    pub semantic: String,
    /// Floats per vertex (`m_stride / 4`).
    pub width: usize,
    /// `width` floats per vertex.
    pub values: Vec<f32>,
}

/// A morphing segment: its index in the instance's mesh and its inputs.
#[derive(Debug, Clone, PartialEq)]
pub struct SegmentMorph {
    /// The segment's index among its mesh's segments.
    pub k: usize,
    pub vertices: usize,
    pub inputs: Vec<Input>,
}

/// A morphing instance.
#[derive(Debug, Clone, PartialEq)]
pub struct InstanceMorph {
    /// Its index among the cluster's `PMeshInstance`s.
    pub instance: usize,
    /// The network's modifier (`PModifierAndInputs.m_modifier`'s name, `Morph23`).
    pub modifier: String,
    pub segments: Vec<SegmentMorph>,
}

/// The game's own fields of a `PMeshInstance` that say how it morphs.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Tags {
    /// `m_animCt` (`+0x30`): the game registers an instance's morph only
    /// when it is above 0.
    pub anim_ct: i32,
    /// `m_mimeCt` (`+0x44`): 1 a position morph (a dynamic mesh), 3 UV, 4
    /// vertex colour (`MorphManager_registerClusterMorphs` `0x006aca10`).
    pub mime_ct: i32,
    /// `m_dObjFlag` (`+0x54`): `0x400` scrolls the UVs (`UvOffsetX/Y`).
    pub dobj_flag: u32,
    /// `m_objID` (`+0x58`).
    pub obj_id: i32,
}

fn u32_at(b: &[u8], o: usize) -> Option<u32> {
    Some(u32::from_le_bytes(b.get(o..o + 4)?.try_into().ok()?))
}

fn f32_at(b: &[u8], o: usize) -> Option<f32> {
    u32_at(b, o).map(f32::from_bits)
}

struct Reader<'a> {
    c: &'a Cluster,
    bytes: &'a [u8],
    fixups: Vec<Vec<PointerFixup>>,
}

impl Reader<'_> {
    fn list(&self, class: &str) -> Option<usize> {
        self.c
            .lists
            .iter()
            .position(|l| self.c.classes[l.class].name == class)
    }

    fn word(&self, list: usize, object: usize, offset: usize) -> Option<u32> {
        let l = &self.c.lists[list];
        if object >= l.count as usize {
            return None;
        }
        u32_at(self.bytes, l.object(u32::try_from(object).ok()?) + offset)
    }

    fn member(&self, class: &str, member: &str) -> Option<usize> {
        self.c.member_offset(class, member).map(|o| o as usize)
    }

    /// The object that `list[object].member` (a plain pointer member of
    /// `class`) points to in `target`.
    fn pointee(
        &self,
        list: usize,
        object: usize,
        class: &str,
        member: &str,
        target: usize,
    ) -> Option<usize> {
        self.fixups[list]
            .iter()
            .find(|f| {
                f.object() as usize == object
                    && f.target_list() as usize == target
                    && f.member() & 0x8000_0000 == 0
                    && self
                        .c
                        .member_by_index(f.member())
                        .is_some_and(|(k, m)| self.c.classes[k].name == class && m.name == member)
            })
            .map(|f| f.target() as usize)
    }

    /// The string a user-fixup pointer of `list[object]` holds (a render
    /// type, a modifier's name).
    fn user_text(&self, list: usize, object: usize) -> Option<String> {
        self.fixups[list]
            .iter()
            .filter(|f| f.object() as usize == object)
            .find_map(PointerFixup::user_fixup)
            .and_then(|u| self.c.user_fixups.get(u as usize))
            .map(|u| u.text.clone())
    }
}

/// Every instance's [`Tags`], in `PMeshInstance` order; empty when the
/// cluster has no such fields.
#[must_use]
pub fn tags(bytes: &[u8]) -> Vec<Tags> {
    let Ok(c) = Cluster::parse(bytes) else {
        return Vec::new();
    };
    let Some(list) = c.list("PMeshInstance") else {
        return Vec::new();
    };
    let at = |m: &str| c.member_offset("PMeshInstance", m).map(|o| o as usize);
    let (Some(anim), Some(mime), Some(flag), Some(id)) = (
        at("m_animCt"),
        at("m_mimeCt"),
        at("m_dObjFlag"),
        at("m_objID"),
    ) else {
        return Vec::new();
    };
    (0..list.count)
        .map(|i| {
            let o = list.object(i);
            Tags {
                anim_ct: u32_at(bytes, o + anim).map_or(0, u32::cast_signed),
                mime_ct: u32_at(bytes, o + mime).map_or(0, u32::cast_signed),
                dobj_flag: u32_at(bytes, o + flag).unwrap_or(0),
                obj_id: u32_at(bytes, o + id).map_or(0, u32::cast_signed),
            }
        })
        .collect()
}

/// The cluster's morphing instances and their inputs. A dynamic mesh whose
/// counts do not add up is left out.
#[must_use]
#[allow(clippy::too_many_lines, clippy::many_single_char_names)] // one walk over the network's lists
pub fn read(bytes: &[u8]) -> Vec<InstanceMorph> {
    let Ok(c) = Cluster::parse(bytes) else {
        return Vec::new();
    };
    let Ok(fixups) = c.pointer_fixups(bytes) else {
        return Vec::new();
    };
    let r = Reader {
        c: &c,
        bytes,
        fixups,
    };
    let lists = (
        r.list("PMeshInstance"),
        r.list("PModifierNetworkDynamicMeshInstance"),
        r.list("PModifierNetworkDynamicMesh"),
        r.list("PDynamicSegmentDesc"),
        r.list("PDynamicDataBlock"),
        r.list("PModifierNetworkDynamicMeshSegment"),
        r.list("PModifierNetwork"),
        r.list("PVertexStream"),
        r.list("PModifierAndInputs"),
    );
    let (
        Some(instances),
        Some(dmis),
        Some(dmeshes),
        Some(descs),
        Some(blocks),
        Some(dsegs),
        Some(networks),
        Some(streams),
        modifiers,
    ) = lists
    else {
        return Vec::new();
    };
    let fields = (
        r.member("PDynamicMesh", "m_segments"),
        r.member("PModifierNetworkDynamicMesh", "m_modifierNetworkInputs"),
        r.member("PModifierNetworkDynamicMesh", "m_segmentInputs"),
        r.member("PDynamicSegmentDesc", "m_elementCount"),
        r.member("PDynamicDataBlock", "m_stride"),
        r.member("PDynamicDataBlock", "m_elementCount"),
        r.member("PDynamicDataBlock", "m_data"),
        r.member(
            "PModifierNetworkDynamicMeshSegment",
            "m_segmentInputGroupStart",
        ),
        r.member("PModifierNetwork", "m_totalInputCount"),
    );
    let (
        Some(o_segments),
        Some(o_inputs),
        Some(o_seg_inputs),
        Some(o_desc_count),
        Some(o_stride),
        Some(o_count),
        Some(o_data),
        Some(o_group_start),
        Some(o_input_count),
    ) = fields
    else {
        return Vec::new();
    };

    // each data block's values: the list's arrays, one block after another
    let block_list = &c.lists[blocks];
    let mut data_at = Vec::with_capacity(block_list.count as usize);
    let mut at = block_list.arrays().start;
    for b in 0..block_list.count as usize {
        data_at.push(at);
        at += r.word(blocks, b, o_data).unwrap_or(0) as usize;
    }
    if at != block_list.arrays().end {
        return Vec::new();
    }
    // the first segment, input and segment input of each dynamic mesh
    let mesh_count = c.lists[dmeshes].count as usize;
    let mut starts = Vec::with_capacity(mesh_count);
    let (mut s0, mut i0, mut g0) = (0usize, 0usize, 0usize);
    for m in 0..mesh_count {
        starts.push((s0, i0, g0));
        s0 += r.word(dmeshes, m, o_segments).unwrap_or(0) as usize;
        i0 += r.word(dmeshes, m, o_inputs).unwrap_or(0) as usize;
        g0 += r.word(dmeshes, m, o_seg_inputs).unwrap_or(0) as usize;
    }
    // a network's modifier name (the first PModifierAndInputs of each network, in order)
    let modifier_name = |network: usize| -> String {
        modifiers
            .and_then(|l| r.user_text(l, network))
            .unwrap_or_default()
    };

    let mut out = Vec::new();
    for i in 0..c.lists[instances].count as usize {
        let Some(dmi) = r.pointee(instances, i, "PMeshInstance", "m_dynamicMeshInstance", dmis)
        else {
            continue;
        };
        let Some(m) = r.pointee(dmis, dmi, "PDynamicMeshInstance", "m_dynamicMesh", dmeshes) else {
            continue;
        };
        let (s0, i0, g0) = starts[m];
        let segs = r.word(dmeshes, m, o_segments).unwrap_or(0) as usize;
        let inputs = r.word(dmeshes, m, o_inputs).unwrap_or(0) as usize;
        let groups = r.word(dmeshes, m, o_seg_inputs).unwrap_or(0) as usize;
        if groups != segs {
            continue;
        }
        let mut morph = InstanceMorph {
            instance: i,
            modifier: String::new(),
            segments: Vec::new(),
        };
        let mut ok = true;
        for k in 0..segs {
            let vertices = r.word(descs, s0 + k, o_desc_count).unwrap_or(0) as usize;
            let g = g0 + k;
            let start = r.word(dsegs, g, o_group_start).unwrap_or(0) as usize;
            let network = r.pointee(
                dsegs,
                g,
                "PModifierNetworkDynamicMeshSegment",
                "m_modifierNetwork",
                networks,
            );
            let n = network.map_or(0, |n| {
                r.word(networks, n, o_input_count).unwrap_or(0) as usize
            });
            if morph.modifier.is_empty()
                && let Some(n) = network
            {
                morph.modifier = modifier_name(n);
            }
            if n == 0 || start + n > inputs {
                ok = false;
                break;
            }
            let mut seg = SegmentMorph {
                k,
                vertices,
                inputs: Vec::new(),
            };
            #[allow(clippy::needless_range_loop)] // b indexes three lists
            for b in (i0 + start)..(i0 + start + n) {
                let stride = r.word(blocks, b, o_stride).unwrap_or(0) as usize;
                let count = r.word(blocks, b, o_count).unwrap_or(0) as usize;
                if stride == 0 || !stride.is_multiple_of(4) || count != vertices {
                    ok = false;
                    break;
                }
                let semantic = r.fixups[blocks]
                    .iter()
                    .find(|f| f.object() as usize == b && f.target_list() as usize == streams)
                    .and_then(|f| r.user_text(streams, f.target() as usize))
                    .unwrap_or_default();
                let width = stride / 4;
                let values: Option<Vec<f32>> = (0..count * width)
                    .map(|v| f32_at(bytes, data_at[b] + v * 4))
                    .collect();
                let Some(values) = values else {
                    ok = false;
                    break;
                };
                seg.inputs.push(Input {
                    semantic,
                    width,
                    values,
                });
            }
            if !ok {
                break;
            }
            morph.segments.push(seg);
        }
        if ok && !morph.segments.is_empty() {
            out.push(morph);
        }
    }
    out
}
