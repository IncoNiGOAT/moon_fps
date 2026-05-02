import argparse
import json
import math
import os
import sys

import bpy
from mathutils import Matrix, Quaternion, Vector

IDENTITY_QUATERNION = [0.0, 0.0, 0.0, 1.0]
IDENTITY_VECTOR = [0.0, 0.0, 0.0]
IDENTITY_SCALE = [1.0, 1.0, 1.0]
def parse_args():
    parser = argparse.ArgumentParser(description="Export Citizen DMX clips through Blender Source Tools.")
    parser.add_argument("--mode", choices=("dump-bind", "export-payload", "export-payload-fbx", "dump-pose", "dump-fbx-pose", "roundtrip-dmx", "roundtrip-fbx", "roundtrip-smd", "retarget-clip", "copy-fbx-to-dmx", "copy-fbx-to-fbx-template", "copy-source-action-to-fbx"), required=True)
    parser.add_argument("--citizen-fbx", required=True, help="Absolute path to citizen.fbx")
    parser.add_argument("--source-fbx", help="Absolute path to the source FBX for retarget-clip mode")
    parser.add_argument("--template-fbx", help="Absolute path to a stock/template FBX for copy-fbx-to-fbx-template mode")
    parser.add_argument("--output", required=True, help="Output json or dmx path depending on mode")
    parser.add_argument("--input-json", help="Input payload JSON for export-payload mode")
    parser.add_argument("--input-dmx", help="Input DMX path for dump-pose mode")
    parser.add_argument("--template-dmx", help="Optional stock animated DMX template for export-payload mode")
    parser.add_argument("--pre-export-json", help="Optional JSON dump of the applied pose before DMX export")
    parser.add_argument("--clip-name", help="Source clip name for retarget-clip mode")
    parser.add_argument("--sequence-name", help="Output sequence name for retarget-clip mode")
    parser.add_argument("--mapping-json", help="Mapping JSON path for retarget-clip mode")
    parser.add_argument("--compensation-json", help="Compensation JSON path for retarget-clip mode")
    parser.add_argument("--include-hands", default="1", help="Whether to include hand and finger mappings in retarget-clip mode")
    parser.add_argument("--source-action", help="Optional action name hint for copy-fbx-to-dmx mode")
    parser.add_argument("--up-axis", choices=("Y", "Z"), default="Y", help="BST export up axis for DMX/SMD roundtrip modes")
    parser.add_argument("--legacy-rotation", default="0", help="Whether to enable BST legacy_rotation for SMD export")
    parser.add_argument("--scene-centimeters", default="0", help="Whether to set the Blender scene to centimeter units before importing assets")
    parser.add_argument("--bst-root", required=True, help="Directory containing the io_scene_valvesource package")

    argv = sys.argv
    if "--" in argv:
        return parser.parse_args(argv[argv.index("--") + 1 :])
    return parser.parse_args([])


def clean_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for datablock_collection in (bpy.data.meshes, bpy.data.armatures, bpy.data.materials, bpy.data.actions, bpy.data.images):
        for item in list(datablock_collection):
            if item.users == 0:
                datablock_collection.remove(item)


def ensure_bst_registered(bst_root):
    if not os.path.isdir(bst_root):
        raise FileNotFoundError(f"Could not find Blender Source Tools root: {bst_root}")

    if bst_root not in sys.path:
        sys.path.insert(0, bst_root)

    import io_scene_valvesource  # type: ignore
    from io_scene_valvesource import utils as vst_utils  # type: ignore

    try:
        io_scene_valvesource.register()
    except ValueError:
        # Classes may already be registered in some Blender sessions.
        pass

    return io_scene_valvesource, vst_utils


def requires_bst(mode):
    return mode in {
        "dump-bind",
        "export-payload",
        "dump-pose",
        "roundtrip-dmx",
        "roundtrip-smd",
        "retarget-clip",
        "copy-fbx-to-dmx",
    }


def import_citizen_fbx(fbx_path):
    if not os.path.isfile(fbx_path):
        raise FileNotFoundError(f"Could not find citizen FBX: {fbx_path}")

    before_names = set(bpy.data.objects.keys())
    bpy.ops.import_scene.fbx(filepath=fbx_path)
    armatures = [bpy.data.objects[name] for name in bpy.data.objects.keys() if name not in before_names and bpy.data.objects[name].type == "ARMATURE"]
    if not armatures:
        raise RuntimeError("No armature found after importing citizen.fbx")
    return armatures[0]


def import_source_fbx(fbx_path):
    if not os.path.isfile(fbx_path):
        raise FileNotFoundError(f"Could not find source FBX: {fbx_path}")

    before_names = set(bpy.data.objects.keys())
    bpy.ops.import_scene.fbx(filepath=fbx_path)
    armatures = [bpy.data.objects[name] for name in bpy.data.objects.keys() if name not in before_names and bpy.data.objects[name].type == "ARMATURE"]
    if not armatures:
        raise RuntimeError("No armature found after importing the source FBX")
    return armatures[0]


def import_template_fbx(fbx_path):
    return import_source_fbx(fbx_path)


def import_template_dmx(dmx_path):
    if not os.path.isfile(dmx_path):
        raise FileNotFoundError(f"Could not find template DMX: {dmx_path}")

    before_names = set(bpy.data.objects.keys())
    bpy.ops.import_scene.smd(filepath=dmx_path)
    armatures = [bpy.data.objects[name] for name in bpy.data.objects.keys() if name not in before_names and bpy.data.objects[name].type == "ARMATURE"]
    if not armatures:
        raise RuntimeError("No armature found after importing the template DMX")
    return armatures[0]


def find_source_action(clip_name):
    clip_name_lower = clip_name.lower()
    candidates = [action for action in bpy.data.actions if clip_name_lower in action.name.lower()]
    if not candidates:
        raise RuntimeError(f"Unable to find an imported action containing '{clip_name}'")

    def rank(action):
        lower = action.name.lower()
        return (
            lower.endswith(f"|{clip_name_lower}"),
            lower.endswith(clip_name_lower),
            ".tak" not in lower,
            len(action.name),
            action.name,
        )

    return sorted(candidates, key=rank, reverse=True)[0]


def find_preferred_action(source_armature, action_name_hint=None):
    current_action = None
    if source_armature.animation_data and source_armature.animation_data.action:
        current_action = source_armature.animation_data.action
        if not action_name_hint or action_name_hint.lower() in current_action.name.lower():
            return current_action

    actions = list(bpy.data.actions)
    if not actions:
        raise RuntimeError("Unable to find any imported actions on the source armature")

    if action_name_hint:
        lowered_hint = action_name_hint.lower()
        hinted_matches = [action for action in actions if lowered_hint in action.name.lower()]
        if hinted_matches:
            return sorted(hinted_matches, key=lambda action: len(action.name))[0]

    if current_action is not None:
        return current_action

    def action_rank(action):
        frame_range = action.frame_range
        return (
            frame_range[1] - frame_range[0],
            len(action.fcurves) if hasattr(action, "fcurves") else 0,
            action.name,
        )

    return sorted(actions, key=action_rank, reverse=True)[0]


def ensure_source_action(source_armature, action):
    animation_data = source_armature.animation_data_create()
    animation_data.action = action
    if hasattr(animation_data, "action_suitable_slots") and animation_data.action_suitable_slots:
        animation_data.action_slot = animation_data.action_suitable_slots[0]


def is_hand_or_finger_mapping(source_name, target_name):
    source = (source_name or "").lower()
    target = (target_name or "").lower()
    return (
        "hand_" in source
        or "thumb_" in source
        or "index_" in source
        or "middle_" in source
        or "ring_" in source
        or "pinky_" in source
        or "hand_" in target
        or "finger_" in target
    )


def load_mapping(mapping_json_path, include_hands):
    with open(mapping_json_path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)

    mapping = {}
    for source_name, target_name in payload.get("mapping", {}).items():
        if not include_hands and is_hand_or_finger_mapping(source_name, target_name):
            continue
        mapping[source_name] = target_name
    return mapping


def load_compensation_offsets(compensation_json_path):
    with open(compensation_json_path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)

    offsets = {}
    for side_data in payload.get("offsets_deg", {}).values():
        for key, value in side_data.items():
            bone_name, axis = key.split(".", 1)
            offsets.setdefault(bone_name, []).append((axis, value))
    return offsets


def apply_compensation(pose_bone, offsets):
    axis_map = {
        "X": Vector((1.0, 0.0, 0.0)),
        "Y": Vector((0.0, 1.0, 0.0)),
        "Z": Vector((0.0, 0.0, 1.0)),
    }
    quat = pose_bone.rotation_quaternion.copy()
    for axis, degrees in offsets:
        axis_vector = axis_map.get(axis)
        if axis_vector is None:
            continue
        quat = quat @ Quaternion(axis_vector, math.radians(degrees))
    pose_bone.rotation_quaternion = quat


def get_rest_local_matrix(armature, bone):
    if bone.parent:
        return bone.parent.matrix_local.inverted_safe() @ bone.matrix_local
    return bone.matrix_local.copy()


def matrix_from_transform(translation, rotation):
    quaternion = Quaternion((rotation[3], rotation[0], rotation[1], rotation[2]))
    return Matrix.Translation(Vector(translation)) @ quaternion.to_matrix().to_4x4()


def canonicalize_root_local_matrix(armature, local_matrix):
    return armature.matrix_world @ local_matrix


def decanonicalize_root_local_matrix(armature, canonical_local_matrix):
    return armature.matrix_world.inverted_safe() @ canonical_local_matrix


def collect_object_transform(armature):
    translation, rotation, scale = armature.matrix_world.decompose()
    return {
        "translation": [translation.x, translation.y, translation.z],
        "rotation": [rotation.x, rotation.y, rotation.z, rotation.w],
        "scale": [scale.x, scale.y, scale.z],
    }


def collect_bind_payload(armature, sequence_name):
    bones = []
    frame_transforms = []
    output_unit_meters = 0.01

    ordered_bones = list(armature.data.bones)
    id_by_name = {bone.name: index for index, bone in enumerate(ordered_bones)}
    for bone in ordered_bones:
        local_matrix = get_rest_local_matrix(armature, bone)
        if bone.parent is None:
            local_matrix = canonicalize_root_local_matrix(armature, local_matrix)
        translation = local_matrix.to_translation()
        rotation = local_matrix.to_quaternion()
        translation_list = [translation.x, translation.y, translation.z]
        rotation_list = [rotation.x, rotation.y, rotation.z, rotation.w]

        bones.append(
            {
                "id": id_by_name[bone.name],
                "name": bone.name,
                "parent_id": -1 if bone.parent is None else id_by_name[bone.parent.name],
                "rest_translation": translation_list,
                "rest_rotation": rotation_list,
            }
        )
        frame_transforms.append(
            {
                "translation": translation_list,
                "rotation": rotation_list,
            }
        )

    return {
        "sequence_name": sequence_name,
        "display_name": sequence_name,
        "model_name": "models/citizen/citizen_noscale.vmdl",
        "skeleton_name": "citizen_noscale1",
        "frame_rate": 30.0,
        "looping": False,
        "output_unit_meters": output_unit_meters,
        "object_transform": collect_object_transform(armature),
        "bones": bones,
        "frames": [
            {
                "index": 0,
                "bone_transforms": frame_transforms,
            }
        ],
    }


def collect_pose_payload(armature, sequence_name):
    bones = []
    frame_transforms = []
    ordered_bones = list(armature.data.bones)
    id_by_name = {bone.name: index for index, bone in enumerate(ordered_bones)}
    pose_bones = armature.pose.bones

    for bone in ordered_bones:
        pose_bone = pose_bones.get(bone.name)
        if pose_bone is None:
            continue

        if pose_bone.parent:
            local_matrix = pose_bone.parent.matrix.inverted_safe() @ pose_bone.matrix
        else:
            local_matrix = pose_bone.matrix.copy()
            local_matrix = canonicalize_root_local_matrix(armature, local_matrix)

        translation = local_matrix.to_translation()
        rotation = local_matrix.to_quaternion()
        translation_list = [translation.x, translation.y, translation.z]
        rotation_list = [rotation.x, rotation.y, rotation.z, rotation.w]

        bones.append(
            {
                "id": id_by_name[bone.name],
                "name": bone.name,
                "parent_id": -1 if bone.parent is None else id_by_name[bone.parent.name],
                "rest_translation": translation_list,
                "rest_rotation": rotation_list,
            }
        )
        frame_transforms.append(
            {
                "translation": translation_list,
                "rotation": rotation_list,
            }
        )

    return {
        "sequence_name": sequence_name,
        "display_name": sequence_name,
        "model_name": "models/citizen/citizen_noscale.vmdl",
        "skeleton_name": "citizen_noscale1",
        "frame_rate": 30.0,
        "looping": False,
        "output_unit_meters": 0.01,
        "object_transform": collect_object_transform(armature),
        "bones": bones,
        "frames": [
            {
                "index": 0,
                "bone_transforms": frame_transforms,
            }
        ],
    }


def ensure_native_action_slot(armature, sequence_name):
    ad = armature.animation_data_create()
    if ad.action and getattr(ad, "action_slot", None):
        action = ad.action
        slot = ad.action_slot
        slot.name_display = sequence_name
        action.name = sequence_name
        return action, slot

    action = bpy.data.actions.new(sequence_name)
    slot = action.slots.new("OBJECT", sequence_name)
    ad.action = action
    suitable_slots = list(getattr(ad, "action_suitable_slots", []))
    if suitable_slots:
        ad.action_slot = suitable_slots[0]
        slot = ad.action_slot
        slot.name_display = sequence_name

    return action, slot


def get_or_create_channelbag(armature, sequence_name, vst_utils=None):
    action, slot = ensure_native_action_slot(armature, sequence_name)
    layer = action.layers[0] if action.layers else action.layers.new(sequence_name)
    strip = layer.strips[0] if layer.strips else layer.strips.new(type="KEYFRAME")
    return strip.channelbag(slot, ensure=True)


def clear_channelbag(channelbag):
    for fcurve in list(channelbag.fcurves):
        channelbag.fcurves.remove(fcurve)
    for group in list(channelbag.groups):
        channelbag.groups.remove(group)


def configure_armature_bst_metadata(armature):
    if hasattr(armature.data, "vs"):
        armature.data.vs.action_selection = "CURRENT"
        armature.data.vs.legacy_rotation = False
    if hasattr(armature, "vs"):
        armature.vs.export = True
        armature.vs.subdir = ""


def apply_payload_to_armature(armature, payload, vst_utils):
    channelbag = get_or_create_channelbag(armature, payload["sequence_name"], vst_utils)
    clear_channelbag(channelbag)
    configure_armature_bst_metadata(armature)

    rest_local_by_name = {bone.name: get_rest_local_matrix(armature, bone) for bone in armature.data.bones}
    pose_bones = {bone.name: bone for bone in armature.pose.bones}
    ordered_bones = [bone["name"] for bone in payload["bones"]]
    payload_bones_by_name = {bone["name"]: bone for bone in payload["bones"]}
    transforms_by_index = [None] * len(ordered_bones)

    scene = bpy.context.scene
    scene.frame_start = 0
    scene.frame_end = max(int(frame["index"]) for frame in payload["frames"])

    ordered_pose_bones = list(armature.pose.bones)

    for frame in sorted(payload["frames"], key=lambda item: int(item["index"])):
        frame_index = int(frame["index"])
        scene.frame_set(frame_index)
        for pose_bone in ordered_pose_bones:
            pose_bone.rotation_mode = "QUATERNION"
            pose_bone.matrix_basis = Matrix.Identity(4)

        for bone_index, transform in enumerate(frame["bone_transforms"]):
            transforms_by_index[bone_index] = transform

        for bone_name, transform in zip(ordered_bones, transforms_by_index):
            pose_bone = pose_bones.get(bone_name)
            if pose_bone is None:
                continue

            rest_local = rest_local_by_name[bone_name]
            desired_local = matrix_from_transform(transform["translation"], transform["rotation"])
            if pose_bone.parent is None:
                desired_local = decanonicalize_root_local_matrix(armature, desired_local)
            basis_matrix = rest_local.inverted_safe() @ desired_local
            pose_bone.matrix_basis = basis_matrix
            pose_bone.keyframe_insert(data_path="location", frame=frame_index, group=bone_name)
            pose_bone.keyframe_insert(data_path="rotation_quaternion", frame=frame_index, group=bone_name)

    scene.frame_set(0)


def retarget_clip_to_armature(source_armature, target_armature, action, mapping, compensation_offsets, sequence_name, vst_utils):
    ensure_source_action(source_armature, action)

    channelbag = get_or_create_channelbag(target_armature, sequence_name, vst_utils)
    clear_channelbag(channelbag)
    configure_armature_bst_metadata(target_armature)

    scene = bpy.context.scene
    start = int(action.frame_range[0])
    end = int(action.frame_range[1])
    scene.frame_start = 0
    scene.frame_end = max(0, end - start)

    ordered_target_pose_bones = list(target_armature.pose.bones)
    target_pose_bones = {bone.name: bone for bone in ordered_target_pose_bones}
    source_pose_bones = {bone.name: bone for bone in source_armature.pose.bones}

    for output_frame, source_frame in enumerate(range(start, end + 1)):
        scene.frame_set(source_frame)

        for pose_bone in ordered_target_pose_bones:
            pose_bone.rotation_mode = "QUATERNION"
            pose_bone.matrix_basis = Matrix.Identity(4)

        for source_name, target_name in mapping.items():
            source_bone = source_pose_bones.get(source_name)
            target_bone = target_pose_bones.get(target_name)
            if source_bone is None or target_bone is None:
                continue

            target_bone.rotation_mode = "QUATERNION"
            target_bone.matrix_basis = source_bone.matrix_basis.copy()

            if target_name in compensation_offsets:
                apply_compensation(target_bone, compensation_offsets[target_name])

            target_bone.keyframe_insert(data_path="location", frame=output_frame, group=target_name)
            target_bone.keyframe_insert(data_path="rotation_quaternion", frame=output_frame, group=target_name)
            target_bone.keyframe_insert(data_path="scale", frame=output_frame, group=target_name)

    scene.frame_set(0)


def get_pose_local_matrix(armature, pose_bone):
    if pose_bone.parent:
        return pose_bone.parent.matrix.inverted_safe() @ pose_bone.matrix

    local_matrix = pose_bone.matrix.copy()
    return canonicalize_root_local_matrix(armature, local_matrix)


def copy_same_skeleton_action_to_armature(source_armature, target_armature, action, sequence_name, vst_utils):
    ensure_source_action(source_armature, action)

    channelbag = get_or_create_channelbag(target_armature, sequence_name, vst_utils)
    clear_channelbag(channelbag)
    configure_armature_bst_metadata(target_armature)

    scene = bpy.context.scene
    start = int(action.frame_range[0])
    end = int(action.frame_range[1])
    scene.frame_start = 0
    scene.frame_end = max(0, end - start)

    source_pose_bones = {bone.name: bone for bone in source_armature.pose.bones}
    ordered_target_pose_bones = list(target_armature.pose.bones)
    target_rest_locals = {
        bone.name: get_rest_local_matrix(target_armature, bone)
        for bone in target_armature.data.bones
    }

    for output_frame, source_frame in enumerate(range(start, end + 1)):
        scene.frame_set(source_frame)

        for target_pose_bone in ordered_target_pose_bones:
            target_pose_bone.rotation_mode = "QUATERNION"
            target_pose_bone.matrix_basis = Matrix.Identity(4)

        for target_pose_bone in ordered_target_pose_bones:
            source_pose_bone = source_pose_bones.get(target_pose_bone.name)
            if source_pose_bone is None:
                continue

            desired_local = get_pose_local_matrix(source_armature, source_pose_bone)
            if target_pose_bone.parent is None:
                desired_local = decanonicalize_root_local_matrix(target_armature, desired_local)

            rest_local = target_rest_locals.get(target_pose_bone.name)
            if rest_local is None:
                continue

            basis_matrix = rest_local.inverted_safe() @ desired_local
            target_pose_bone.matrix_basis = basis_matrix
            target_pose_bone.keyframe_insert(data_path="location", frame=output_frame, group=target_pose_bone.name)
            target_pose_bone.keyframe_insert(data_path="rotation_quaternion", frame=output_frame, group=target_pose_bone.name)
            target_pose_bone.keyframe_insert(data_path="scale", frame=output_frame, group=target_pose_bone.name)

    scene.frame_set(0)


def parse_bool_flag(value):
    return str(value).strip().lower() not in ("0", "false", "no", "off", "")


def configure_scene_units(use_centimeters):
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 0.01 if use_centimeters else 1.0


def configure_export(armature, output_path, vst_utils, export_format="DMX", up_axis="Y", legacy_rotation=False):
    scene = bpy.context.scene
    output_dir = os.path.dirname(output_path)
    os.makedirs(output_dir, exist_ok=True)
    export_root = output_dir
    if os.path.basename(output_dir).lower() == "anims":
        export_root = os.path.dirname(output_dir) or output_dir
    scene.vs.export_path = export_root
    scene.vs.export_format = export_format
    scene.vs.dmx_encoding = "9"
    scene.vs.dmx_format = "22_modeldoc"
    scene.vs.up_axis = up_axis
    scene.vs.qc_compile = False
    scene.vs.use_kv2 = False
    armature.data.vs.action_selection = "CURRENT"
    armature.data.vs.legacy_rotation = legacy_rotation
    vst_utils.State.update_scene(scene)


def reset_armature_pose_to_rest(armature):
    for pose_bone in armature.pose.bones:
        pose_bone.rotation_mode = "QUATERNION"
        pose_bone.matrix_basis = Matrix.Identity(4)

    bpy.context.view_layer.update()


def export_current_action(armature, output_path):
    expected_output = os.path.abspath(output_path)
    output_dir = os.path.dirname(expected_output)
    sequence_name = os.path.splitext(os.path.basename(expected_output))[0]
    extension = os.path.splitext(expected_output)[1] or ".dmx"
    candidate_outputs = [
        expected_output,
        os.path.join(output_dir, f"{sequence_name}{extension}"),
        os.path.join(output_dir, "anims", f"{sequence_name}{extension}"),
        os.path.join(output_dir, "anims", "anims", f"{sequence_name}{extension}"),
    ]

    if armature.animation_data is None or armature.animation_data.action_slot is None:
        raise RuntimeError("The armature does not have an active animation slot for export")

    armature.animation_data.action_slot.name_display = sequence_name

    bpy.ops.object.select_all(action="DESELECT")
    armature.select_set(True)
    bpy.context.view_layer.objects.active = armature
    reset_armature_pose_to_rest(armature)

    for candidate in candidate_outputs:
        if os.path.exists(candidate):
            os.remove(candidate)

    bpy.ops.export_scene.smd(collection="", export_scene=False)

    actual_output = next((path for path in candidate_outputs if os.path.exists(path)), None)
    if actual_output:
        print(f"Exported DMX to {actual_output}")
        return actual_output

    if not os.path.exists(expected_output):
        raise FileNotFoundError(
            f"Expected Blender Source Tools to export '{expected_output}' "
            f"(checked {candidate_outputs})"
        )

    return expected_output


def get_export_objects_for_armature(armature, include_meshes):
    export_objects = [armature]
    if not include_meshes:
        return export_objects

    for candidate in bpy.data.objects:
        if candidate == armature or candidate.type != "MESH":
            continue

        has_matching_modifier = any(
            modifier.type == "ARMATURE" and modifier.object == armature
            for modifier in candidate.modifiers
        )
        if candidate.parent == armature or has_matching_modifier:
            export_objects.append(candidate)

    return export_objects


def export_current_action_fbx(armature, output_path, include_meshes=False):
    expected_output = os.path.abspath(output_path)
    os.makedirs(os.path.dirname(expected_output), exist_ok=True)

    bpy.ops.object.select_all(action="DESELECT")
    export_objects = get_export_objects_for_armature(armature, include_meshes)
    for export_object in export_objects:
        export_object.select_set(True)
    bpy.context.view_layer.objects.active = armature

    if armature.animation_data and armature.animation_data.action:
        frame_start, frame_end = armature.animation_data.action.frame_range
        bpy.context.scene.frame_start = int(frame_start)
        bpy.context.scene.frame_end = int(frame_end)
        bpy.context.scene.frame_set(int(frame_start))

    bpy.ops.export_scene.fbx(
        filepath=expected_output,
        check_existing=False,
        path_mode="AUTO",
        use_selection=True,
        global_scale=1.0,
        axis_forward="-Z",
        axis_up="Y",
        primary_bone_axis="Y",
        secondary_bone_axis="X",
        apply_unit_scale=True,
        object_types={"ARMATURE", "MESH"} if include_meshes else {"ARMATURE"},
        use_armature_deform_only=False,
        add_leaf_bones=False,
        bake_anim=True,
        bake_anim_use_all_bones=True,
        bake_anim_use_all_actions=False,
        bake_anim_use_nla_strips=False,
        bake_anim_simplify_factor=0.0,
    )

    if not os.path.exists(expected_output):
        raise FileNotFoundError(f"Expected Blender FBX export to create '{expected_output}'")

    return expected_output


def main():
    args = parse_args()
    vst_utils = None
    if requires_bst(args.mode):
        _, vst_utils = ensure_bst_registered(args.bst_root)
    clean_scene()
    configure_scene_units(parse_bool_flag(args.scene_centimeters))

    if args.mode == "dump-bind":
        armature = import_template_dmx(args.template_dmx) if args.template_dmx else import_citizen_fbx(args.citizen_fbx)
        os.makedirs(os.path.dirname(args.output), exist_ok=True)
        payload = collect_bind_payload(armature, "citizen_bind_cache")
        with open(args.output, "w", encoding="utf-8") as handle:
            json.dump(payload, handle, indent=2)
        return

    if args.mode == "dump-pose":
        source_dmx = args.input_dmx or args.template_dmx
        if not source_dmx:
            raise RuntimeError("dump-pose mode requires --input-dmx or --template-dmx")
        armature = import_template_dmx(source_dmx)
        bpy.context.scene.frame_set(0)
        os.makedirs(os.path.dirname(args.output), exist_ok=True)
        payload = collect_pose_payload(armature, "citizen_pose_dump")
        with open(args.output, "w", encoding="utf-8") as handle:
            json.dump(payload, handle, indent=2)
        return

    if args.mode == "dump-fbx-pose":
        if not args.source_fbx:
            raise RuntimeError("dump-fbx-pose mode requires --source-fbx")

        source_armature = import_source_fbx(args.source_fbx)
        action = find_preferred_action(source_armature, args.source_action)
        ensure_source_action(source_armature, action)
        start = int(action.frame_range[0])
        bpy.context.scene.frame_set(start)
        os.makedirs(os.path.dirname(args.output), exist_ok=True)
        payload = collect_pose_payload(source_armature, action.name)
        with open(args.output, "w", encoding="utf-8") as handle:
            json.dump(payload, handle, indent=2)
        return

    if args.mode == "roundtrip-dmx":
        source_dmx = args.input_dmx or args.template_dmx
        if not source_dmx:
            raise RuntimeError("roundtrip-dmx mode requires --input-dmx or --template-dmx")
        armature = import_template_dmx(source_dmx)
        configure_export(armature, args.output, vst_utils)
        export_current_action(armature, args.output)
        return

    if args.mode == "roundtrip-fbx":
        if not args.source_fbx:
            raise RuntimeError("roundtrip-fbx mode requires --source-fbx")
        armature = import_source_fbx(args.source_fbx)
        export_current_action_fbx(armature, args.output)
        return

    if args.mode == "roundtrip-smd":
        if not args.source_fbx:
            raise RuntimeError("roundtrip-smd mode requires --source-fbx")
        armature = import_source_fbx(args.source_fbx)
        configure_export(
            armature,
            args.output,
            vst_utils,
            export_format="SMD",
            up_axis=args.up_axis,
            legacy_rotation=parse_bool_flag(args.legacy_rotation),
        )
        export_current_action(armature, args.output)
        return

    if args.mode == "retarget-clip":
        if not args.source_fbx:
            raise RuntimeError("retarget-clip mode requires --source-fbx")
        if not args.clip_name:
            raise RuntimeError("retarget-clip mode requires --clip-name")
        if not args.sequence_name:
            raise RuntimeError("retarget-clip mode requires --sequence-name")
        if not args.mapping_json:
            raise RuntimeError("retarget-clip mode requires --mapping-json")
        if not args.compensation_json:
            raise RuntimeError("retarget-clip mode requires --compensation-json")

        target_armature = import_template_dmx(args.template_dmx or args.input_dmx) if (args.template_dmx or args.input_dmx) else import_citizen_fbx(args.citizen_fbx)
        source_armature = import_source_fbx(args.source_fbx)
        action = find_source_action(args.clip_name)
        include_hands = str(args.include_hands).strip().lower() not in ("0", "false", "no", "off")
        mapping = load_mapping(args.mapping_json, include_hands)
        compensation_offsets = load_compensation_offsets(args.compensation_json)
        retarget_clip_to_armature(source_armature, target_armature, action, mapping, compensation_offsets, args.sequence_name, vst_utils)
        configure_export(target_armature, args.output, vst_utils)
        export_current_action(target_armature, args.output)
        return

    if args.mode == "copy-fbx-to-dmx":
        target_armature = import_template_dmx(args.template_dmx or args.input_dmx) if (args.template_dmx or args.input_dmx) else import_citizen_fbx(args.citizen_fbx)
        source_armature = import_source_fbx(args.source_fbx)
        source_action = find_preferred_action(source_armature, args.source_action)
        sequence_name = args.sequence_name or os.path.splitext(os.path.basename(args.output))[0]
        copy_same_skeleton_action_to_armature(source_armature, target_armature, source_action, sequence_name, vst_utils)
        configure_export(target_armature, args.output, vst_utils)
        export_current_action(target_armature, args.output)
        return

    if args.mode == "copy-fbx-to-fbx-template":
        if not args.source_fbx:
            raise RuntimeError("copy-fbx-to-fbx-template mode requires --source-fbx")
        if not args.template_fbx:
            raise RuntimeError("copy-fbx-to-fbx-template mode requires --template-fbx")

        source_armature = import_source_fbx(args.source_fbx)
        target_armature = import_template_fbx(args.template_fbx)
        source_action = find_preferred_action(source_armature, args.source_action)
        sequence_name = args.sequence_name or os.path.splitext(os.path.basename(args.output))[0]
        copy_same_skeleton_action_to_armature(source_armature, target_armature, source_action, sequence_name, vst_utils)
        export_current_action_fbx(target_armature, args.output)
        return

    if args.mode == "copy-source-action-to-fbx":
        if not args.source_fbx:
            raise RuntimeError("copy-source-action-to-fbx mode requires --source-fbx")

        source_armature = import_source_fbx(args.source_fbx)
        source_action = find_preferred_action(source_armature, args.source_action)
        ensure_source_action(source_armature, source_action)
        export_current_action_fbx(source_armature, args.output, include_meshes=True)
        return

    if not args.input_json:
        raise RuntimeError(f"{args.mode} mode requires --input-json")

    with open(args.input_json, "r", encoding="utf-8") as handle:
        payload = json.load(handle)

    if args.mode == "export-payload-fbx":
        armature = import_citizen_fbx(args.citizen_fbx)
        apply_payload_to_armature(armature, payload, vst_utils)
        if args.pre_export_json:
            os.makedirs(os.path.dirname(args.pre_export_json), exist_ok=True)
            pre_export_payload = collect_pose_payload(armature, payload["sequence_name"])
            with open(args.pre_export_json, "w", encoding="utf-8") as handle:
                json.dump(pre_export_payload, handle, indent=2)
        export_current_action_fbx(armature, args.output, include_meshes=False)
        return

    armature = import_template_dmx(args.template_dmx) if args.template_dmx else import_citizen_fbx(args.citizen_fbx)
    apply_payload_to_armature(armature, payload, vst_utils)
    if args.pre_export_json:
        os.makedirs(os.path.dirname(args.pre_export_json), exist_ok=True)
        pre_export_payload = collect_pose_payload(armature, payload["sequence_name"])
        with open(args.pre_export_json, "w", encoding="utf-8") as handle:
            json.dump(pre_export_payload, handle, indent=2)
    configure_export(armature, args.output, vst_utils)
    export_current_action(armature, args.output)


if __name__ == "__main__":
    main()
