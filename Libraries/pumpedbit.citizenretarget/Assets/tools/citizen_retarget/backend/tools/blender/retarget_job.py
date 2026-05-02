from __future__ import annotations

import argparse
import importlib
import io
import json
import math
import os
import re
import sys
import traceback
import uuid
from contextlib import redirect_stderr, redirect_stdout
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import addon_utils
import bpy
from mathutils import Euler, Matrix, Quaternion, Vector


class PipelineError(Exception):
    pass


@dataclass
class StageResult:
    stage_id: str
    enabled: bool
    status: str
    started_at: str
    finished_at: str
    warnings: list[str] = field(default_factory=list)
    metrics: dict[str, Any] = field(default_factory=dict)
    outputs: dict[str, Any] = field(default_factory=dict)
    debug_notes: list[str] = field(default_factory=list)
    error: str | None = None

    def to_dict(self) -> dict[str, Any]:
        return {
            "stageId": self.stage_id,
            "enabled": self.enabled,
            "status": self.status,
            "startedAt": self.started_at,
            "finishedAt": self.finished_at,
            "warnings": self.warnings,
            "metrics": self.metrics,
            "outputs": self.outputs,
            "debugNotes": self.debug_notes,
            "error": self.error,
        }


@dataclass
class RunContext:
    recipe_path: Path
    recipe: dict[str, Any]
    source_profile_path: Path
    source_profile: dict[str, Any]
    target_profile_path: Path
    target_profile: dict[str, Any]
    input_path: Path
    output_root: Path
    selected_action_name: str | None
    target_pose_preset_override: str | None
    bone_map_overrides_path: Path | None
    bone_map_overrides: dict[str, Any]
    run_id: str
    run_dir: Path
    debug_enabled: bool
    save_intermediate_blend_files: bool
    save_stage_metrics: bool
    stage_results: list[StageResult] = field(default_factory=list)
    global_warnings: list[str] = field(default_factory=list)

    @property
    def logs_dir(self) -> Path:
        return self.run_dir / "logs"

    @property
    def metrics_dir(self) -> Path:
        return self.run_dir / "stage_metrics"

    @property
    def snapshots_dir(self) -> Path:
        return self.run_dir / "snapshots"

    @property
    def exports_dir(self) -> Path:
        return self.run_dir / "exports"


class Stage:
    stage_id: str = ""

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        raise NotImplementedError


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def script_root() -> Path:
    return Path(__file__).resolve().parent


def ensure_dir(path: Path) -> Path:
    path.mkdir(parents=True, exist_ok=True)
    return path


def sanitize_name(value: str) -> str:
    cleaned = re.sub(r"[^A-Za-z0-9._-]+", "_", value.strip())
    return cleaned.strip("._") or "unnamed"


ROKOKO_ADDON_NAME = "rokoko_studio_live_blender"
RESOLVED_ROKOKO_ADDON_NAME: str | None = None


def write_json(path: Path, payload: dict[str, Any] | list[Any]) -> None:
    ensure_dir(path.parent)
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")


def write_text(path: Path, text: str) -> None:
    ensure_dir(path.parent)
    path.write_text(text, encoding="utf-8")


def to_float_list(value: Any) -> list[float] | None:
    if value is None:
        return None
    return [float(component) for component in value]


def vector_distance(a: list[float] | None, b: list[float] | None) -> float | None:
    if a is None or b is None or len(a) != len(b):
        return None
    return math.sqrt(sum((left - right) ** 2 for left, right in zip(a, b)))


def round_float(value: float | None, digits: int = 6) -> float | None:
    if value is None:
        return None
    return round(float(value), digits)


def action_leaf_name(name: str) -> str:
    return name.split("|")[-1].strip()


def load_json_file(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def resolve_named_json(base_dir: Path, value: str | None) -> Path:
    if not value:
        raise PipelineError("Missing JSON reference.")

    candidate = Path(value)
    if candidate.is_file():
        return candidate.resolve()

    if candidate.suffix.lower() != ".json":
        candidate = candidate.with_suffix(".json")

    resolved = (base_dir / candidate.name).resolve()
    if resolved.is_file():
        return resolved

    raise PipelineError(f"Could not resolve JSON file for '{value}' under {base_dir}.")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--recipe", required=True)
    parser.add_argument("--input", required=True)
    parser.add_argument("--output-root", required=True)
    parser.add_argument("--action")
    parser.add_argument("--source-profile")
    parser.add_argument("--target-profile")
    parser.add_argument("--target-pose-preset")
    parser.add_argument("--bone-map-overrides")
    parser.add_argument("--run-id")

    argv = sys.argv
    if "--" in argv:
        argv = argv[argv.index("--") + 1 :]
    else:
        argv = argv[1:]
    return parser.parse_args(argv)


def reset_scene() -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)


def dedupe_preserve_order(values: list[str]) -> list[str]:
    seen: set[str] = set()
    result: list[str] = []
    for value in values:
        if value not in seen:
            seen.add(value)
            result.append(value)
    return result


def resolve_target_bone_name_for_slot(target_profile: dict[str, Any], slot: str) -> str | None:
    canonical_slots = target_profile.get("canonicalSlots") or {}
    ik_target_pairs = target_profile.get("ikTargetPairs") or {}
    target_bone = canonical_slots.get(slot) or ik_target_pairs.get(slot)
    if target_bone is None:
        return None
    value = str(target_bone).strip()
    return value or None


def normalize_external_bone_map_overrides(
    target_profile: dict[str, Any],
    raw_overrides: dict[str, Any] | None,
) -> dict[str, Any]:
    if not isinstance(raw_overrides, dict):
        return {
            "slots": {},
            "summary": {
                "provided": False,
                "enabledSlotCount": 0,
                "requiredSlotCount": 0,
                "optionalSlotCount": 0,
                "disabledSlotCount": 0,
                "lockedSlotCount": 0,
                "userOverrideSlotCount": 0,
                "missingTargetBoneSlots": [],
                "invalidSlots": [],
            },
            "metadata": {},
        }

    slots_payload = raw_overrides.get("slots")
    if not isinstance(slots_payload, dict):
        slots_payload = {}

    normalized_slots: dict[str, dict[str, Any]] = {}
    disabled_slots: list[str] = []
    invalid_slots: list[str] = []
    missing_target_bone_slots: list[str] = []
    locked_slots: list[str] = []
    user_override_slots: list[str] = []
    required_slots: list[str] = []
    optional_slots: list[str] = []

    for slot, payload in sorted(slots_payload.items()):
        if not isinstance(payload, dict):
            invalid_slots.append(str(slot))
            continue

        slot_id = str(slot).strip()
        if not slot_id:
            continue

        enabled = bool(payload.get("enabled", True))
        if not enabled:
            disabled_slots.append(slot_id)
            continue

        source_bone = str(payload.get("sourceBone") or "").strip()
        target_bone = str(payload.get("targetBone") or "").strip()
        if not target_bone:
            target_bone = resolve_target_bone_name_for_slot(target_profile, slot_id) or ""

        if not source_bone:
            invalid_slots.append(slot_id)
            continue

        if not target_bone:
            missing_target_bone_slots.append(slot_id)
            continue

        required = bool(payload.get("required", False))
        locked = bool(payload.get("locked", False))
        user_overridden = bool(payload.get("userOverridden", True))

        normalized_slots[slot_id] = {
            "sourceBone": source_bone,
            "targetBone": target_bone,
            "required": required,
            "enabled": True,
            "locked": locked,
            "userOverridden": user_overridden,
            "group": payload.get("group"),
            "mappingOrigin": str(payload.get("mappingOrigin") or "external_override"),
        }

        if required:
            required_slots.append(slot_id)
        else:
            optional_slots.append(slot_id)
        if locked:
            locked_slots.append(slot_id)
        if user_overridden:
            user_override_slots.append(slot_id)

    return {
        "slots": normalized_slots,
        "summary": {
            "provided": bool(normalized_slots or disabled_slots or invalid_slots or missing_target_bone_slots),
            "enabledSlotCount": len(normalized_slots),
            "requiredSlotCount": len(required_slots),
            "optionalSlotCount": len(optional_slots),
            "disabledSlotCount": len(disabled_slots),
            "lockedSlotCount": len(locked_slots),
            "userOverrideSlotCount": len(user_override_slots),
            "disabledSlots": disabled_slots,
            "requiredSlots": required_slots,
            "optionalSlots": optional_slots,
            "lockedSlots": locked_slots,
            "userOverrideSlots": user_override_slots,
            "missingTargetBoneSlots": missing_target_bone_slots,
            "invalidSlots": invalid_slots,
        },
        "metadata": raw_overrides.get("metadata") or {},
    }


TARGET_FINGER_CHAIN_CITIZEN_012_SLOT_IDS = frozenset(
    {
        "finger_index_0_L",
        "finger_index_0_R",
        "finger_index_1_L",
        "finger_index_1_R",
        "finger_index_2_L",
        "finger_index_2_R",
        "finger_middle_0_L",
        "finger_middle_0_R",
        "finger_middle_1_L",
        "finger_middle_1_R",
        "finger_middle_2_L",
        "finger_middle_2_R",
        "finger_ring_0_L",
        "finger_ring_0_R",
        "finger_ring_1_L",
        "finger_ring_1_R",
        "finger_ring_2_L",
        "finger_ring_2_R",
    }
)

TARGET_FINGER_CHAIN_CITIZEN_012_KEY_TO_TARGET = {
    "leftIndexProximal": "finger_index_0_L",
    "custom_bone_index_01_l": "finger_index_0_L",
    "leftIndexMedial": "finger_index_1_L",
    "leftIndexDistal": "finger_index_2_L",
    "leftMiddleProximal": "finger_middle_0_L",
    "leftMiddleMedial": "finger_middle_1_L",
    "leftMiddleDistal": "finger_middle_2_L",
    "leftRingProximal": "finger_ring_0_L",
    "leftRingMedial": "finger_ring_1_L",
    "leftRingDistal": "finger_ring_2_L",
    "rightIndexProximal": "finger_index_0_R",
    "custom_bone_index_01_r": "finger_index_0_R",
    "rightIndexMedial": "finger_index_1_R",
    "rightIndexDistal": "finger_index_2_R",
    "rightMiddleProximal": "finger_middle_0_R",
    "rightMiddleMedial": "finger_middle_1_R",
    "rightMiddleDistal": "finger_middle_2_R",
    "rightRingProximal": "finger_ring_0_R",
    "rightRingMedial": "finger_ring_1_R",
    "rightRingDistal": "finger_ring_2_R",
}

TARGET_FINGER_CHAIN_CITIZEN_012_DETECTION_OVERRIDES = {
    "leftIndexProximal": ["finger_index_0_l"],
    "custom_bone_index_01_l": ["index_01_l", "finger_index_0_l"],
    "leftIndexMedial": ["finger_index_1_l"],
    "leftIndexDistal": ["finger_index_2_l"],
    "leftMiddleProximal": ["finger_middle_0_l"],
    "leftMiddleMedial": ["finger_middle_1_l"],
    "leftMiddleDistal": ["finger_middle_2_l"],
    "leftRingProximal": ["finger_ring_0_l"],
    "leftRingMedial": ["finger_ring_1_l"],
    "leftRingDistal": ["finger_ring_2_l"],
    "rightIndexProximal": ["finger_index_0_r"],
    "custom_bone_index_01_r": ["index_01_r", "finger_index_0_r"],
    "rightIndexMedial": ["finger_index_1_r"],
    "rightIndexDistal": ["finger_index_2_r"],
    "rightMiddleProximal": ["finger_middle_0_r"],
    "rightMiddleMedial": ["finger_middle_1_r"],
    "rightMiddleDistal": ["finger_middle_2_r"],
    "rightRingProximal": ["finger_ring_0_r"],
    "rightRingMedial": ["finger_ring_1_r"],
    "rightRingDistal": ["finger_ring_2_r"],
}


def normalize_target_finger_chain_remap_mode(remap_mode: Any) -> str:
    return str(remap_mode or "").strip().lower() or "none"


def resolve_target_finger_chain_remap_mode(
    context: RunContext,
    config: dict[str, Any],
) -> str:
    override_metadata = dict((context.bone_map_overrides or {}).get("metadata") or {})
    explicit_mode = (
        override_metadata.get("targetFingerChainRemapMode")
        or config.get("targetFingerChainRemapMode")
        or config.get("target_finger_chain_remap_mode")
    )
    normalized_mode = normalize_target_finger_chain_remap_mode(explicit_mode)
    if normalized_mode != "none":
        return normalized_mode

    source_profile_id = str(override_metadata.get("sourceProfileId") or "").strip().lower()
    mapping_profile_id = str(override_metadata.get("mappingProfileId") or "").strip().lower()
    import_hands = bool(override_metadata.get("importHands", False))
    if import_hands and source_profile_id == "quaternius_ual2" and mapping_profile_id == "ual2_to_citizen":
        return "citizen_012"

    return "none"


def resolve_source_facing_euler_degrees(
    context: RunContext,
    config: dict[str, Any],
) -> list[float]:
    override_metadata = dict((context.bone_map_overrides or {}).get("metadata") or {})
    explicit_value = override_metadata.get("sourceFacingEulerDegrees")
    if explicit_value is None:
        explicit_value = config.get("sourceFacingEulerDegrees")
    if explicit_value is None:
        legacy_yaw_value = override_metadata.get("sourceFacingYawDegrees")
        if legacy_yaw_value is None:
            legacy_yaw_value = config.get("sourceFacingYawDegrees")
        if legacy_yaw_value is None:
            return [0.0, 0.0, 0.0]

        try:
            return [0.0, 0.0, float(legacy_yaw_value)]
        except (TypeError, ValueError):
            raise PipelineError(f"Invalid sourceFacingYawDegrees value: {legacy_yaw_value!r}")
    if explicit_value is None:
        return [0.0, 0.0, 0.0]

    try:
        if not isinstance(explicit_value, list) or len(explicit_value) != 3:
            raise PipelineError(
                f"sourceFacingEulerDegrees must be a 3-item list, got {explicit_value!r}"
            )
        return [float(explicit_value[0]), float(explicit_value[1]), float(explicit_value[2])]
    except (TypeError, ValueError):
        raise PipelineError(f"Invalid sourceFacingEulerDegrees value: {explicit_value!r}")


def build_import_settings(config: dict[str, Any]) -> dict[str, Any]:
    return {
        "automatic_bone_orientation": bool(config.get("automaticBoneOrientation", False)),
        "anim_offset": float(config.get("animOffset", 1.0)),
    }


def capture_operator_output(callback) -> tuple[str, list[str]]:
    buffer = io.StringIO()
    with redirect_stdout(buffer), redirect_stderr(buffer):
        callback()
    text = buffer.getvalue()
    warning_lines = []
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("WARNING:") or stripped.startswith("ERROR:"):
            warning_lines.append(stripped)
    return text, dedupe_preserve_order(warning_lines)


def normalize_addon_probe_text(value: Any) -> str:
    return str(value or "").strip().lower().replace("_", "-")


def normalize_addon_path(value: Any) -> str:
    text = str(value or "").strip().strip('"')
    if not text:
        return ""
    try:
        return os.path.normcase(os.path.abspath(text))
    except Exception:
        return os.path.normcase(text)


def configured_rokoko_addon_init_path() -> str:
    configured = normalize_addon_path(os.environ.get("CITIZEN_RETARGET_ROKOKO_ADDON_PATH"))
    if not configured:
        return ""
    if os.path.isdir(configured):
        return normalize_addon_path(os.path.join(configured, "__init__.py"))
    return configured


def module_addon_init_path(module: Any) -> str:
    return normalize_addon_path(getattr(module, "__file__", "") or "")


def module_matches_configured_rokoko_path(module: Any, configured_init_path: str) -> bool:
    if not configured_init_path:
        return False
    module_init_path = module_addon_init_path(module)
    return module_init_path == configured_init_path or (
        module_init_path
        and normalize_addon_path(os.path.dirname(module_init_path))
        == normalize_addon_path(os.path.dirname(configured_init_path))
    )


def is_rokoko_addon_module(module: Any) -> bool:
    module_name = str(getattr(module, "__name__", "") or "").strip()
    bl_info = getattr(module, "bl_info", {}) or {}
    addon_name = str(bl_info.get("name") or "").strip() if isinstance(bl_info, dict) else ""
    module_path = str(getattr(module, "__file__", "") or "").strip()
    haystack = normalize_addon_probe_text(" ".join([module_name, addon_name, module_path]))
    return "rokoko" in haystack and (
        "studio" in haystack
        or "live" in haystack
        or "rokoko-studio-live-blender" in haystack
    )


def find_rokoko_addon_module(preferred_name: str = ROKOKO_ADDON_NAME) -> Any | None:
    configured_init_path = configured_rokoko_addon_init_path()
    configured_match = None
    matches: list[Any] = []
    for candidate in addon_utils.modules():
        if module_matches_configured_rokoko_path(candidate, configured_init_path):
            configured_match = candidate
            break
        candidate_name = str(getattr(candidate, "__name__", "") or "").strip()
        if candidate_name == preferred_name:
            return candidate
        if is_rokoko_addon_module(candidate):
            matches.append(candidate)
    return configured_match or (matches[0] if matches else None)


def enable_addon(addon_name: str) -> tuple[Any | None, str | None]:
    global RESOLVED_ROKOKO_ADDON_NAME

    module = find_rokoko_addon_module(addon_name)
    if module is None:
        configured_path = configured_rokoko_addon_init_path()
        if configured_path:
            raise PipelineError(
                "Could not find the configured Rokoko addon in this Blender install. "
                f"Configured path: {configured_path}. "
                "Clear the Rokoko Addon override to auto-scan, or point it to the addon folder/__init__.py used by this Blender."
            )
        raise PipelineError(
            "Could not find the Rokoko Studio Live Blender addon. "
            "Install and enable it in the same Blender executable used by CARL."
        )

    module_name = str(getattr(module, "__name__", "") or "").strip()
    if not module_name:
        raise PipelineError("Found a Rokoko addon candidate, but it has no Blender module name.")

    if not addon_utils.check(module_name)[1]:
        addon_utils.enable(module_name, default_set=False, persistent=False)

    if not addon_utils.check(module_name)[1]:
        raise PipelineError(f"Could not enable Blender addon '{module_name}'.")

    RESOLVED_ROKOKO_ADDON_NAME = module_name
    module = sys.modules.get(module_name) or module

    bl_info = getattr(module, "bl_info", {}) or {}
    version = bl_info.get("version")
    version_text = ".".join(str(part) for part in version) if version else None
    return module, version_text


def find_armatures() -> list[Any]:
    return [obj for obj in bpy.data.objects if obj.type == "ARMATURE"]


def require_single_armature() -> Any:
    armatures = find_armatures()
    if len(armatures) != 1:
        raise PipelineError(f"Expected exactly one armature, found {len(armatures)}.")
    return armatures[0]


def ensure_animation_data(armature: Any) -> Any:
    if armature.animation_data is None:
        armature.animation_data_create()
    return armature.animation_data


def assign_action_to_armature(armature: Any, action: Any) -> None:
    animation_data = ensure_animation_data(armature)
    animation_data.action = action
    slots = getattr(action, "slots", None)
    if slots:
        try:
            animation_data.action_slot = slots[0]
        except Exception:
            pass


def resolve_action(armature: Any, requested_action_name: str | None) -> Any:
    actions = list(bpy.data.actions)
    if not actions:
        raise PipelineError("No actions were found after import.")

    if requested_action_name:
        exact = [action for action in actions if action.name == requested_action_name]
        if len(exact) == 1:
            return exact[0]

        requested_leaf = action_leaf_name(requested_action_name).lower()
        by_leaf = [
            action
            for action in actions
            if action_leaf_name(action.name).lower() == requested_leaf
        ]
        if len(by_leaf) == 1:
            return by_leaf[0]
        if len(by_leaf) > 1:
            raise PipelineError(
                f"Action '{requested_action_name}' is ambiguous. "
                f"Matching actions: {[action.name for action in by_leaf]}"
            )

        requested_normalized = normalize_action_lookup_name(requested_leaf)
        if requested_normalized in {"atpose", "tpose"}:
            by_pose_alias = [
                action
                for action in actions
                if normalize_action_lookup_name(action_leaf_name(action.name)) in {"atpose", "tpose"}
            ]
            if len(by_pose_alias) == 1:
                return by_pose_alias[0]
            if len(by_pose_alias) > 1:
                raise PipelineError(
                    f"Action '{requested_action_name}' matched multiple T-pose aliases. "
                    f"Matching actions: {[action.name for action in by_pose_alias]}"
                )

        if len(actions) == 1:
            return actions[0]
        raise PipelineError(f"Could not find action '{requested_action_name}'.")

    active_action = armature.animation_data.action if armature.animation_data else None
    if active_action is not None:
        return active_action
    if len(actions) == 1:
        return actions[0]
    raise PipelineError(
        "Input contains multiple actions and no explicit --action was provided."
    )


def normalize_action_lookup_name(name: str) -> str:
    return "".join(character for character in str(name).lower() if character.isalnum())


def extract_action_fcurves(action: Any) -> list[Any]:
    legacy_fcurves = getattr(action, "fcurves", None)
    if legacy_fcurves is not None:
        try:
            return list(legacy_fcurves)
        except Exception:
            pass

    collected: list[Any] = []
    seen_keys: set[tuple[str, int]] = set()
    slots = list(getattr(action, "slots", None) or [])
    layers = list(getattr(action, "layers", None) or [])

    for layer in layers:
        for strip in getattr(layer, "strips", []):
            channelbag_getter = getattr(strip, "channelbag", None)
            if channelbag_getter is None:
                continue

            candidate_slots = slots or [None]
            for slot in candidate_slots:
                try:
                    channelbag = channelbag_getter(slot) if slot is not None else channelbag_getter()
                except TypeError:
                    try:
                        channelbag = channelbag_getter()
                    except Exception:
                        continue
                except Exception:
                    continue

                if channelbag is None:
                    continue

                fcurves = getattr(channelbag, "fcurves", None)
                if fcurves is None:
                    continue

                try:
                    iterator = list(fcurves)
                except Exception:
                    continue

                for fcurve in iterator:
                    key = (
                        str(getattr(fcurve, "data_path", "") or ""),
                        int(getattr(fcurve, "array_index", -1)),
                    )
                    if key in seen_keys:
                        continue
                    seen_keys.add(key)
                    collected.append(fcurve)

    return collected


def extract_animated_bone_names(action: Any) -> list[str]:
    seen: list[str] = []
    for fcurve in extract_action_fcurves(action):
        parts = fcurve.data_path.split('"')
        if len(parts) >= 2 and parts[1] not in seen:
            seen.append(parts[1])
    return seen


def detect_leaf_bones(bone_names: list[str], profile: dict[str, Any]) -> list[str]:
    tokens = profile.get("leafBoneTokens", ["_leaf"])
    result = []
    for bone_name in bone_names:
        lowered = bone_name.lower()
        if any(token.lower() in lowered for token in tokens):
            result.append(bone_name)
    return result


def local_pose_matrix(pose_bone: Any) -> Matrix:
    if pose_bone.parent is not None:
        return pose_bone.parent.matrix.inverted() @ pose_bone.matrix
    return pose_bone.matrix.copy()


def bone_rest_local_matrix(pose_bone: Any) -> Matrix:
    if pose_bone.parent is not None:
        return pose_bone.parent.bone.matrix_local.inverted() @ pose_bone.bone.matrix_local
    return pose_bone.bone.matrix_local.copy()


def bone_rest_length(pose_bone: Any) -> float:
    return float((pose_bone.bone.tail_local - pose_bone.bone.head_local).length)


def safe_normalize(vector: Vector, fallback: Vector | None = None) -> Vector:
    candidate = vector.copy()
    if candidate.length > 1e-8:
        candidate.normalize()
        return candidate
    if fallback is not None:
        candidate = fallback.copy()
        if candidate.length > 1e-8:
            candidate.normalize()
            return candidate
    return Vector((0.0, 1.0, 0.0))


def scaled_vector(vector: Vector, scale: float) -> Vector:
    return Vector((vector.x * scale, vector.y * scale, vector.z * scale))


def bone_rest_direction_in_parent_space(pose_bone: Any) -> Vector:
    rest_local = bone_rest_local_matrix(pose_bone)
    return safe_normalize(rest_local.to_3x3() @ Vector((0.0, 1.0, 0.0)))


def keyframe_pose_bone(pose_bone: Any, frame: int) -> None:
    pose_bone.keyframe_insert(data_path="location", frame=frame, group=pose_bone.name)
    pose_bone.keyframe_insert(
        data_path="rotation_quaternion",
        frame=frame,
        group=pose_bone.name,
    )


def build_converted_target_delta(source_pose_bone: Any, rest_payload: dict[str, Any]) -> Matrix:
    source_reference_local = rest_payload["sourceReferenceLocal"]
    converter = rest_payload["converter"]
    delta = source_reference_local.inverted() @ local_pose_matrix(source_pose_bone)
    return converter @ delta @ converter.inverted()


def apply_converted_local_delta(
    source_pose_bone: Any,
    target_pose_bone: Any,
    rest_payload: dict[str, Any],
    *,
    allow_translation: bool,
) -> Matrix:
    target_delta = build_converted_target_delta(source_pose_bone, rest_payload)
    if not allow_translation:
        target_delta.translation = (0.0, 0.0, 0.0)
    target_pose_bone.matrix_basis = target_delta
    return target_delta


def solve_two_bone_joint(
    start: Vector,
    desired_end: Vector,
    pole_hint: Vector,
    upper_length: float,
    lower_length: float,
) -> tuple[Vector, Vector]:
    reach_vector = desired_end - start
    reach_direction = safe_normalize(reach_vector)
    distance = max(reach_vector.length, 1e-6)
    min_reach = max(abs(upper_length - lower_length) + 1e-5, 1e-5)
    max_reach = max(upper_length + lower_length - 1e-5, min_reach)
    clamped_distance = max(min_reach, min(distance, max_reach))

    pole_vector = pole_hint - start
    pole_projected = pole_vector - reach_direction * pole_vector.dot(reach_direction)
    if pole_projected.length <= 1e-6:
        fallback_axis = Vector((0.0, 0.0, 1.0))
        pole_projected = fallback_axis - reach_direction * fallback_axis.dot(reach_direction)
    if pole_projected.length <= 1e-6:
        fallback_axis = Vector((1.0, 0.0, 0.0))
        pole_projected = fallback_axis - reach_direction * fallback_axis.dot(reach_direction)
    pole_direction = safe_normalize(pole_projected)

    upper_projection = (
        (clamped_distance * clamped_distance + upper_length * upper_length - lower_length * lower_length)
        / (2.0 * clamped_distance)
    )
    height_sq = max(upper_length * upper_length - upper_projection * upper_projection, 0.0)
    joint = (
        start
        + reach_direction * upper_projection
        + pole_direction * math.sqrt(height_sq)
    )
    clamped_end = start + reach_direction * clamped_distance
    return joint, clamped_end


def align_bone_to_world_direction(pose_bone: Any, desired_direction_world: Vector) -> None:
    parent_matrix = pose_bone.parent.matrix.copy() if pose_bone.parent is not None else Matrix.Identity(4)
    desired_direction_parent = safe_normalize(
        parent_matrix.to_3x3().inverted() @ desired_direction_world,
        fallback=bone_rest_direction_in_parent_space(pose_bone),
    )
    rest_local = bone_rest_local_matrix(pose_bone)
    rest_direction_parent = bone_rest_direction_in_parent_space(pose_bone)
    rotation = rest_direction_parent.rotation_difference(desired_direction_parent).to_matrix().to_4x4()
    pose_bone.matrix = parent_matrix @ rotation @ rest_local


def matrix_to_rows(matrix: Matrix) -> list[list[float]]:
    return [[round_float(value) for value in row] for row in matrix]


def rows_to_matrix(rows: list[list[float]]) -> Matrix:
    return Matrix(rows)


def quaternion_angular_difference_degrees(lhs: Any, rhs: Any) -> float:
    return round_float(math.degrees(lhs.rotation_difference(rhs).angle))


def object_rotation_euler_degrees(obj: Any) -> list[float]:
    rotation = obj.matrix_world.to_euler("XYZ")
    return [round_float(math.degrees(value)) for value in rotation]


def object_delta_rotation_euler_degrees(obj: Any) -> list[float]:
    return [round_float(math.degrees(value)) for value in obj.delta_rotation_euler]


def object_scale_values(obj: Any) -> list[float]:
    return [round_float(value) for value in obj.matrix_world.to_scale()]


def apply_source_preparation(source_armature: Any, prep_mode: str) -> dict[str, Any]:
    normalized_mode = str(prep_mode or "").strip().lower() or "none"
    before_rotation = object_rotation_euler_degrees(source_armature)
    delta_before = object_delta_rotation_euler_degrees(source_armature)

    if normalized_mode in {"none", "off", "preserve"}:
        return {
            "mode": normalized_mode,
            "applied": False,
            "rotationBeforeDegrees": before_rotation,
            "rotationAfterDegrees": before_rotation,
            "deltaRotationBeforeDegrees": delta_before,
            "deltaRotationAfterDegrees": delta_before,
        }

    if normalized_mode == "ual_remove_z_180":
        source_armature.rotation_mode = "XYZ"
        source_armature.delta_rotation_euler = Euler(
            (
                source_armature.delta_rotation_euler.x,
                source_armature.delta_rotation_euler.y,
                source_armature.delta_rotation_euler.z + math.radians(180.0),
            ),
            "XYZ",
        )
        bpy.context.view_layer.update()
        after_rotation = object_rotation_euler_degrees(source_armature)
        delta_after = object_delta_rotation_euler_degrees(source_armature)
        return {
            "mode": normalized_mode,
            "applied": True,
            "rotationBeforeDegrees": before_rotation,
            "rotationAfterDegrees": after_rotation,
            "deltaRotationBeforeDegrees": delta_before,
            "deltaRotationAfterDegrees": delta_after,
            "notes": [
                "Applied manual source prep via delta rotation because the source action owns object-level rotation_euler fcurves.",
            ],
        }

    raise PipelineError(f"Unsupported sourcePreparationMode '{prep_mode}'.")


def quaternion_to_list(value: Quaternion) -> list[float]:
    return [round_float(value.w), round_float(value.x), round_float(value.y), round_float(value.z)]


def quaternion_from_list(value: list[float]) -> Quaternion:
    if len(value) != 4:
        raise PipelineError(f"Expected a 4-item quaternion, got {value}.")
    return Quaternion((float(value[0]), float(value[1]), float(value[2]), float(value[3])))


def matrix_basis_rotation_quaternion(pose_bone: Any) -> Quaternion:
    _, rotation, _ = pose_bone.matrix_basis.decompose()
    return rotation


def capture_pose_basis_state(
    armature: Any,
    bone_names: list[str] | None = None,
) -> dict[str, list[list[float]]]:
    allowed = set(bone_names) if bone_names is not None else None
    state: dict[str, list[list[float]]] = {}
    for pose_bone in armature.pose.bones:
        if allowed is not None and pose_bone.name not in allowed:
            continue
        state[pose_bone.name] = matrix_to_rows(pose_bone.matrix_basis.copy())
    return state


def restore_pose_basis_state(
    armature: Any,
    state: dict[str, list[list[float]]],
) -> list[str]:
    missing_bones: list[str] = []
    for bone_name, basis_rows in state.items():
        pose_bone = armature.pose.bones.get(bone_name)
        if pose_bone is None:
            missing_bones.append(bone_name)
            continue
        pose_bone.matrix_basis = rows_to_matrix(basis_rows)
    bpy.context.view_layer.update()
    return missing_bones


def capture_current_pose_rotation_state(
    armature: Any,
    bone_names: list[str] | None = None,
) -> dict[str, list[float]]:
    allowed = set(bone_names) if bone_names is not None else None
    state: dict[str, list[float]] = {}
    for pose_bone in armature.pose.bones:
        if allowed is not None and pose_bone.name not in allowed:
            continue
        state[pose_bone.name] = quaternion_to_list(matrix_basis_rotation_quaternion(pose_bone))
    return state


def apply_pose_rotation_state(
    armature: Any,
    rotation_state: dict[str, list[float]],
) -> list[str]:
    missing_bones: list[str] = []
    for bone_name, quaternion_values in rotation_state.items():
        pose_bone = armature.pose.bones.get(bone_name)
        if pose_bone is None:
            missing_bones.append(bone_name)
            continue
        location, _, scale = pose_bone.matrix_basis.decompose()
        pose_bone.matrix_basis = Matrix.LocRotScale(
            location,
            quaternion_from_list(quaternion_values),
            scale,
        )
    bpy.context.view_layer.update()
    return missing_bones


def capture_action_frame_rotation_state(
    armature: Any,
    action: Any,
    frame: int,
    bone_names: list[str] | None = None,
) -> dict[str, list[float]]:
    scene = bpy.context.scene
    previous_frame = scene.frame_current
    previous_action = armature.animation_data.action if armature.animation_data else None
    previous_slot = armature.animation_data.action_slot if armature.animation_data else None

    assign_action_to_armature(armature, action)
    scene.frame_set(int(frame))
    bpy.context.view_layer.update()
    captured_state = capture_current_pose_rotation_state(armature, bone_names)

    if previous_action is not None:
        assign_action_to_armature(armature, previous_action)
        if previous_slot is not None:
            try:
                armature.animation_data.action_slot = previous_slot
            except Exception:
                pass
    scene.frame_set(previous_frame)
    bpy.context.view_layer.update()
    return captured_state


def rotation_values_to_quaternion(
    rotation_values: list[float],
    rotation_format: str,
) -> Quaternion:
    normalized_format = str(rotation_format or "").strip().lower()
    if normalized_format == "quaternion":
        return quaternion_from_list(rotation_values)
    if normalized_format == "euler_xyz_degrees":
        if len(rotation_values) != 3:
            raise PipelineError(
                f"Expected a 3-item XYZ Euler degrees rotation, got {rotation_values}."
            )
        return Euler(
            tuple(math.radians(float(value)) for value in rotation_values),
            "XYZ",
        ).to_quaternion()
    raise PipelineError(f"Unsupported pose rotation format '{rotation_format}'.")


def normalize_source_armature_basis_for_backend(
    source_armature: Any,
    target_armature: Any,
    config: dict[str, Any],
) -> dict[str, Any]:
    basis_mode = str(config.get("sourceObjectBasisMode", "match_target_rotation") or "").strip().lower()
    if not basis_mode:
        basis_mode = "match_target_rotation"

    tolerance_degrees = float(config.get("sourceObjectBasisToleranceDegrees", 0.5))
    explicit_rotation_values = config.get("sourceObjectRotationEulerDegrees")
    facing_euler_degrees = list(config.get("sourceFacingEulerDegrees", [0.0, 0.0, 0.0]) or [0.0, 0.0, 0.0])

    source_before_rotation = object_rotation_euler_degrees(source_armature)
    source_before_scale = object_scale_values(source_armature)
    target_rotation = object_rotation_euler_degrees(target_armature)
    target_scale = object_scale_values(target_armature)

    has_facing_override = any(abs(float(value)) > 0.001 for value in facing_euler_degrees)

    if basis_mode in {"off", "none", "preserve"} and explicit_rotation_values is None and not has_facing_override:
        return {
            "mode": basis_mode,
            "applied": False,
            "toleranceDegrees": tolerance_degrees,
            "sourceRotationBeforeDegrees": source_before_rotation,
            "sourceRotationAfterDegrees": source_before_rotation,
            "sourceScaleBefore": source_before_scale,
            "sourceScaleAfter": source_before_scale,
            "targetRotationDegrees": target_rotation,
            "targetScale": target_scale,
            "desiredSourceRotationDegrees": source_before_rotation,
            "angleDeltaBeforeDegrees": 0.0,
            "angleDeltaAfterDegrees": 0.0,
        }

    if explicit_rotation_values is not None:
        if not isinstance(explicit_rotation_values, list) or len(explicit_rotation_values) != 3:
            raise PipelineError(
                "sourceObjectRotationEulerDegrees must be a 3-item list of degrees."
            )
        desired_rotation = [float(value) for value in explicit_rotation_values]
        desired_quaternion = Euler(
            tuple(math.radians(value) for value in desired_rotation),
            "XYZ",
        ).to_quaternion()
        strategy = "explicit_source_rotation"
    elif has_facing_override:
        desired_rotation = list(source_before_rotation)
        desired_rotation[0] += float(facing_euler_degrees[0])
        desired_rotation[1] += float(facing_euler_degrees[1])
        desired_rotation[2] += float(facing_euler_degrees[2])
        desired_quaternion = Euler(
            tuple(math.radians(value) for value in desired_rotation),
            "XYZ",
        ).to_quaternion()
        strategy = "source_facing_euler"
    elif basis_mode == "match_target_rotation":
        desired_rotation = target_rotation
        desired_quaternion = target_armature.matrix_world.to_quaternion()
        strategy = "match_target_rotation"
    else:
        raise PipelineError(f"Unsupported sourceObjectBasisMode '{basis_mode}'.")

    source_before_quaternion = source_armature.matrix_world.to_quaternion()
    angle_delta_before = quaternion_angular_difference_degrees(
        source_before_quaternion,
        desired_quaternion,
    )
    applied = angle_delta_before > tolerance_degrees

    if applied:
        source_armature.rotation_mode = "QUATERNION"
        source_armature.rotation_quaternion = desired_quaternion
        bpy.context.view_layer.update()

    source_after_rotation = object_rotation_euler_degrees(source_armature)
    source_after_scale = object_scale_values(source_armature)
    source_after_quaternion = source_armature.matrix_world.to_quaternion()
    angle_delta_after = quaternion_angular_difference_degrees(
        source_after_quaternion,
        desired_quaternion,
    )

    return {
        "mode": basis_mode,
        "strategy": strategy,
        "applied": applied,
        "toleranceDegrees": round_float(tolerance_degrees),
        "sourceRotationBeforeDegrees": source_before_rotation,
        "sourceRotationAfterDegrees": source_after_rotation,
        "sourceScaleBefore": source_before_scale,
        "sourceScaleAfter": source_after_scale,
        "targetRotationDegrees": target_rotation,
        "targetScale": target_scale,
        "desiredSourceRotationDegrees": [round_float(value) for value in desired_rotation],
        "angleDeltaBeforeDegrees": angle_delta_before,
        "angleDeltaAfterDegrees": angle_delta_after,
    }


def normalize_slot_values(value: Any) -> list[str]:
    if value is None:
        return []
    if isinstance(value, list):
        return [str(item) for item in value]
    return [str(value)]


def resolve_source_slot_bone_name(profile: dict[str, Any], armature: Any, slot: str) -> str | None:
    mapping = profile.get("canonicalMapping", {})
    optional_mapping = profile.get("optionalCanonicalMapping", {})
    candidates = normalize_slot_values(mapping.get(slot)) + normalize_slot_values(optional_mapping.get(slot))
    for candidate in candidates:
        if armature.pose.bones.get(candidate) is not None:
            return candidate
    return None


def resolve_target_slot_bone_name(profile: dict[str, Any], armature: Any, slot: str) -> str | None:
    mapping = profile.get("canonicalSlots", {})
    for candidate in normalize_slot_values(mapping.get(slot)):
        if armature.pose.bones.get(candidate) is not None:
            return candidate
    return None


def resolve_source_pose_spec(
    source_profile: dict[str, Any],
    config: dict[str, Any],
) -> dict[str, Any]:
    profile_spec = dict(source_profile.get("retargetPoseSource") or {})
    config_spec = dict(config.get("sourcePose") or {})
    spec = {**profile_spec, **config_spec}
    mode = str(spec.get("mode", "action_frame") or "").strip().lower()
    if mode not in {"action_frame", "rest_pose"}:
        raise PipelineError(f"Unsupported source retarget pose mode '{mode}'.")
    action_name = str(spec.get("actionName") or "").strip()
    frame = int(spec.get("frame", 0))
    return {
        "mode": mode,
        "actionName": action_name,
        "frame": frame,
    }


def resolve_target_pose_preset(
    target_profile: dict[str, Any],
    config: dict[str, Any],
) -> tuple[str, dict[str, Any]]:
    preset_id = str(
        config.get("targetPosePresetId")
        or target_profile.get("defaultRetargetPosePreset")
        or "citizen_t_pose_v1"
    ).strip()
    if not preset_id:
        raise PipelineError("Target pose preset id is missing.")

    presets = target_profile.get("retargetPosePresets") or {}
    preset = dict(presets.get(preset_id) or {})
    if not preset:
        raise PipelineError(f"Target pose preset '{preset_id}' was not found in the target profile.")

    rotation_format = str(preset.get("rotationFormat", "euler_xyz_degrees") or "").strip()
    bones = dict(preset.get("bones") or {})
    if not bones:
        raise PipelineError(f"Target pose preset '{preset_id}' does not define any bones.")
    return preset_id, {
        "rotationFormat": rotation_format,
        "bones": bones,
    }


def resolve_bone_groups(profile: dict[str, Any], bone_names: list[str]) -> dict[str, list[str]]:
    groups = {
        key: normalize_slot_values(value)
        for key, value in (profile.get("boneGroups") or {}).items()
    }
    if "log_only" not in groups:
        groups["log_only"] = normalize_slot_values(
            profile.get("baselineHandling", {}).get("logOnlyBones", [])
        )

    known_non_deform = set()
    for group_name, group_bones in groups.items():
        if group_name == "deform":
            continue
        known_non_deform.update(group_bones)

    if "deform" not in groups:
        groups["deform"] = [bone_name for bone_name in bone_names if bone_name not in known_non_deform]

    return groups


def resolve_animation_export_bone_names(profile: dict[str, Any], armature: Any) -> list[str]:
    export_config = dict(profile.get("animationExport") or {})
    if not export_config:
        return [bone.name for bone in armature.data.bones]

    bone_names = [bone.name for bone in armature.data.bones]
    groups = resolve_bone_groups(profile, bone_names)
    include_group_names = [str(name) for name in export_config.get("includeBoneGroups", [])]
    include_bones = [str(name) for name in export_config.get("includeBones", [])]
    exclude_bones = {str(name) for name in export_config.get("excludeBones", [])}

    available_bones = set(bone_names)
    resolved: list[str] = []
    seen: set[str] = set()

    def add_bone_name(bone_name: str) -> None:
        if (
            not bone_name
            or bone_name in seen
            or bone_name in exclude_bones
            or bone_name not in available_bones
        ):
            return
        seen.add(bone_name)
        resolved.append(bone_name)

    for group_name in include_group_names:
        for bone_name in groups.get(group_name, []):
            add_bone_name(bone_name)

    for bone_name in include_bones:
        add_bone_name(bone_name)

    return resolved or bone_names


def find_root_bone_name(armature: Any, profile: dict[str, Any]) -> str | None:
    for candidate in profile.get("rootBoneCandidates", []):
        if armature.pose.bones.get(candidate) is not None:
            return candidate

    pose_bones = list(armature.pose.bones)
    preferred_tokens = ("hips", "pelvis", "root")
    for token in preferred_tokens:
        for bone in pose_bones:
            normalized = bone.name.replace(" ", "").replace("_", "").replace("-", "").lower()
            leaf_name = normalized.split(":")[-1]
            if leaf_name == token:
                return bone.name

    root_parentless = [bone.name for bone in armature.data.bones if bone.parent is None]
    if len(root_parentless) == 1 and armature.pose.bones.get(root_parentless[0]) is not None:
        return root_parentless[0]

    return None


def get_root_location(armature: Any, root_bone_name: str | None) -> list[float] | None:
    if root_bone_name and armature.pose.bones.get(root_bone_name) is not None:
        location = (armature.matrix_world @ armature.pose.bones[root_bone_name].matrix).translation
        return to_float_list(location)
    return to_float_list(armature.matrix_world.translation)


def build_action_metrics(armature: Any, action: Any, profile: dict[str, Any]) -> dict[str, Any]:
    assign_action_to_armature(armature, action)
    start = int(action.frame_range[0])
    end = int(action.frame_range[1])
    scene = bpy.context.scene
    previous_frame = scene.frame_current
    root_bone_name = find_root_bone_name(armature, profile)
    animated_bones = extract_animated_bone_names(action)

    root_positions: list[list[float] | None] = []
    total_path_length = 0.0
    previous_location = None

    for frame in range(start, end + 1):
        scene.frame_set(frame)
        current_location = get_root_location(armature, root_bone_name)
        root_positions.append(current_location)
        if previous_location is not None:
            step = vector_distance(previous_location, current_location)
            total_path_length += step or 0.0
        previous_location = current_location

    scene.frame_set(previous_frame)

    root_start = root_positions[0] if root_positions else None
    root_end = root_positions[-1] if root_positions else None
    root_displacement = vector_distance(root_start, root_end)

    return {
        "name": action.name,
        "leafName": action_leaf_name(action.name),
        "frameRange": [start, end],
        "animatedBoneCount": len(animated_bones),
        "animatedBoneNames": animated_bones,
        "rootBoneName": root_bone_name,
        "rootStartLocation": root_start,
        "rootEndLocation": root_end,
        "rootDisplacement": round_float(root_displacement),
        "rootPathLength": round_float(total_path_length),
    }


def collect_armature_metrics(
    armature: Any,
    profile: dict[str, Any],
    selected_action_name: str | None,
    allow_missing_action: bool = False,
) -> dict[str, Any]:
    actions = list(bpy.data.actions)
    selected_action = None
    active_action = armature.animation_data.action if armature.animation_data else None

    if selected_action_name is not None or active_action is not None or not allow_missing_action:
        selected_action = resolve_action(armature, selected_action_name)
        assign_action_to_armature(armature, selected_action)

    bone_names = [bone.name for bone in armature.data.bones]
    leaf_bones = detect_leaf_bones(bone_names, profile)

    return {
        "armatureName": armature.name,
        "objectCount": len(bpy.data.objects),
        "armatureCount": len(find_armatures()),
        "boneCount": len(bone_names),
        "boneNames": bone_names,
        "leafBones": leaf_bones,
        "actionCount": len(actions),
        "actionNames": [action.name for action in actions],
        "boneGroups": resolve_bone_groups(profile, bone_names),
        "selectedAction": build_action_metrics(armature, selected_action, profile) if selected_action else None,
    }


def collect_scene_metrics(
    profile: dict[str, Any],
    selected_action_name: str | None,
) -> dict[str, Any]:
    armature = require_single_armature()
    return collect_armature_metrics(
        armature,
        profile,
        selected_action_name,
        allow_missing_action=False,
    )


def bone_world_head(armature: Any, bone_name: str) -> Vector | None:
    pose_bone = armature.pose.bones.get(bone_name)
    if pose_bone is None:
        return None
    return armature.matrix_world @ pose_bone.head


def dominant_axis_from_ranges(axis_ranges: dict[str, float]) -> str | None:
    if not axis_ranges:
        return None
    return max(axis_ranges.items(), key=lambda item: item[1])[0]


def count_sign_changes(values: list[float], epsilon: float = 1e-4) -> int:
    previous_sign = 0
    changes = 0
    for value in values:
        if abs(value) <= epsilon:
            continue
        sign = 1 if value > 0.0 else -1
        if previous_sign and sign != previous_sign:
            changes += 1
        previous_sign = sign
    return changes


def summarize_relative_trajectory(samples: list[list[float]]) -> dict[str, Any]:
    if not samples:
        return {
            "sampleCount": 0,
            "axisRanges": {},
            "dominantAxis": None,
            "firstFrame": None,
            "lastFrame": None,
            "loopClosureDelta": None,
            "samples": [],
        }

    axis_names = ("x", "y", "z")
    axis_ranges = {}
    for axis_index, axis_name in enumerate(axis_names):
        values = [sample[axis_index] for sample in samples]
        axis_ranges[axis_name] = round_float(max(values) - min(values))

    return {
        "sampleCount": len(samples),
        "axisRanges": axis_ranges,
        "dominantAxis": dominant_axis_from_ranges(axis_ranges),
        "firstFrame": [round_float(value) for value in samples[0]],
        "lastFrame": [round_float(value) for value in samples[-1]],
        "loopClosureDelta": round_float(vector_distance(samples[0], samples[-1])),
        "samples": [[round_float(value) for value in sample] for sample in samples],
    }


def capture_relative_trajectories(
    armature: Any,
    action: Any,
    tracked_bone_names: dict[str, str],
    reference_bone_name: str,
) -> tuple[dict[str, dict[str, Any]], list[str]]:
    missing_bones = []
    if armature.pose.bones.get(reference_bone_name) is None:
        missing_bones.append(reference_bone_name)

    for bone_name in tracked_bone_names.values():
        if armature.pose.bones.get(bone_name) is None:
            missing_bones.append(bone_name)

    if missing_bones:
        return {}, dedupe_preserve_order(missing_bones)

    scene = bpy.context.scene
    previous_frame = scene.frame_current
    previous_action = armature.animation_data.action if armature.animation_data else None
    previous_slot = armature.animation_data.action_slot if armature.animation_data else None

    assign_action_to_armature(armature, action)
    frame_start, frame_end = [int(value) for value in action.frame_range]

    collected: dict[str, list[list[float]]] = {slot: [] for slot in tracked_bone_names}
    for frame in range(frame_start, frame_end + 1):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        reference_head = bone_world_head(armature, reference_bone_name)
        if reference_head is None:
            missing_bones.append(reference_bone_name)
            break
        for slot, bone_name in tracked_bone_names.items():
            tracked_head = bone_world_head(armature, bone_name)
            if tracked_head is None:
                missing_bones.append(bone_name)
                continue
            relative = tracked_head - reference_head
            collected[slot].append([float(relative.x), float(relative.y), float(relative.z)])

    if previous_action is not None:
        assign_action_to_armature(armature, previous_action)
        if previous_slot is not None:
            try:
                armature.animation_data.action_slot = previous_slot
            except Exception:
                pass
    scene.frame_set(previous_frame)
    bpy.context.view_layer.update()

    if missing_bones:
        return {}, dedupe_preserve_order(missing_bones)

    summaries = {
        slot: summarize_relative_trajectory(samples)
        for slot, samples in collected.items()
    }
    return summaries, []


def compare_slot_trajectories(
    source_slot_summary: dict[str, Any],
    target_slot_summary: dict[str, Any],
) -> dict[str, Any]:
    source_axis_ranges = source_slot_summary.get("axisRanges") or {}
    target_axis_ranges = target_slot_summary.get("axisRanges") or {}
    source_dominant_axis = source_slot_summary.get("dominantAxis")
    first_frame_delta = None
    if source_slot_summary.get("firstFrame") and target_slot_summary.get("firstFrame"):
        first_frame_delta = round_float(
            vector_distance(source_slot_summary["firstFrame"], target_slot_summary["firstFrame"])
        )

    target_range_on_source_axis = (
        round_float(target_axis_ranges.get(source_dominant_axis))
        if source_dominant_axis is not None
        else None
    )
    source_range_on_source_axis = (
        round_float(source_axis_ranges.get(source_dominant_axis))
        if source_dominant_axis is not None
        else None
    )

    return {
        "sourceDominantAxis": source_dominant_axis,
        "targetDominantAxis": target_slot_summary.get("dominantAxis"),
        "dominantAxisMatches": source_dominant_axis == target_slot_summary.get("dominantAxis"),
        "sourceRangeOnSourceDominantAxis": source_range_on_source_axis,
        "targetRangeOnSourceDominantAxis": target_range_on_source_axis,
        "firstFrameDelta": first_frame_delta,
        "sourceLoopClosureDelta": source_slot_summary.get("loopClosureDelta"),
        "targetLoopClosureDelta": target_slot_summary.get("loopClosureDelta"),
    }


def build_ankle_alternation_summary(
    source_trajectories: dict[str, dict[str, Any]],
    target_trajectories: dict[str, dict[str, Any]],
) -> dict[str, Any]:
    source_left = source_trajectories.get("ankle_L") or {}
    source_right = source_trajectories.get("ankle_R") or {}
    target_left = target_trajectories.get("ankle_L") or {}
    target_right = target_trajectories.get("ankle_R") or {}

    source_axis_ranges_left = source_left.get("axisRanges") or {}
    source_axis_ranges_right = source_right.get("axisRanges") or {}
    pair_axis_ranges = {
        axis_name: round_float(
            float(source_axis_ranges_left.get(axis_name, 0.0))
            + float(source_axis_ranges_right.get(axis_name, 0.0))
        )
        for axis_name in ("x", "y", "z")
    }
    pair_dominant_axis = dominant_axis_from_ranges(pair_axis_ranges)

    summary = {
        "sourcePairDominantAxis": pair_dominant_axis,
        "sourcePairAxisRanges": pair_axis_ranges,
        "sourceSignChanges": None,
        "targetSignChangesOnSourceAxis": None,
    }
    if pair_dominant_axis is None:
        return summary

    def build_difference_series(
        left_summary: dict[str, Any],
        right_summary: dict[str, Any],
        axis_name: str,
    ) -> list[float]:
        left_series = left_summary.get("samples") or []
        right_series = right_summary.get("samples") or []
        if not left_series or not right_series:
            return []
        axis_index = {"x": 0, "y": 1, "z": 2}[axis_name]
        return [
            float(left_series[index][axis_index]) - float(right_series[index][axis_index])
            for index in range(min(len(left_series), len(right_series)))
        ]

    source_series = build_difference_series(source_left, source_right, pair_dominant_axis)
    target_series = build_difference_series(target_left, target_right, pair_dominant_axis)
    summary["sourceSignChanges"] = count_sign_changes(source_series) if source_series else None
    summary["targetSignChangesOnSourceAxis"] = count_sign_changes(target_series) if target_series else None
    return summary


def infer_canonical_slot_from_backend_mapping(
    source_bone_name: str,
    target_bone_name: str,
    required_bone_map: dict[str, dict[str, str]],
) -> str | None:
    for slot, mapping in required_bone_map.items():
        if (
            source_bone_name == mapping.get("sourceBone")
            or target_bone_name == mapping.get("targetBone")
        ):
            return slot
    return None


def collect_rokoko_generated_bone_map(
    scene: Any,
    required_bone_map: dict[str, dict[str, str]],
) -> tuple[list[dict[str, Any]], list[str]]:
    generated: list[dict[str, Any]] = []
    warnings: list[str] = []
    seen_target_bones: set[str] = set()

    for item in getattr(scene, "rsl_retargeting_bone_list", []):
        source_bone_name = str(getattr(item, "bone_name_source", "") or "").strip()
        target_bone_name = str(getattr(item, "bone_name_target", "") or "").strip()
        bone_key = str(getattr(item, "bone_name_key", "") or "").strip()
        is_custom = bool(getattr(item, "is_custom", False))

        canonical_slot = infer_canonical_slot_from_backend_mapping(
            source_bone_name,
            target_bone_name,
            required_bone_map,
        )
        matching_mapping = required_bone_map.get(canonical_slot) if canonical_slot else None
        generated.append(
            {
                "sourceBone": source_bone_name or None,
                "targetBone": target_bone_name or None,
                "boneKey": bone_key or None,
                "canonicalSlot": canonical_slot,
                "isCustom": is_custom,
                "required": bool((matching_mapping or {}).get("required", False)),
                "userOverridden": bool((matching_mapping or {}).get("userOverridden", False)),
                "mappingOrigin": (matching_mapping or {}).get("mappingOrigin"),
            }
        )

        if not target_bone_name:
            warnings.append(f"Rokoko auto-mapping left source bone '{source_bone_name}' unmapped.")
            continue
        if target_bone_name in seen_target_bones:
            warnings.append(f"Rokoko auto-mapping produced duplicate target bone '{target_bone_name}'.")
            continue
        seen_target_bones.add(target_bone_name)

    return generated, dedupe_preserve_order(warnings)


def get_rokoko_addon_module_name() -> str:
    if RESOLVED_ROKOKO_ADDON_NAME:
        return RESOLVED_ROKOKO_ADDON_NAME

    module = find_rokoko_addon_module()
    if module is None:
        return ROKOKO_ADDON_NAME
    return str(getattr(module, "__name__", "") or ROKOKO_ADDON_NAME).strip() or ROKOKO_ADDON_NAME


def import_rokoko_submodule(relative_module_name: str) -> Any:
    base_name = get_rokoko_addon_module_name()
    module_name = f"{base_name}.{relative_module_name}"
    try:
        return importlib.import_module(module_name)
    except Exception as first_exception:
        suffix = f".{relative_module_name}"
        for loaded_name, loaded_module in sys.modules.items():
            normalized_name = normalize_addon_probe_text(loaded_name)
            if loaded_name.endswith(suffix) and "rokoko" in normalized_name:
                return loaded_module
        raise first_exception


def load_rokoko_detection_manager_module() -> Any:
    return import_rokoko_submodule("core.detection_manager")


def load_rokoko_custom_schemes_manager_module() -> Any:
    return import_rokoko_submodule("core.custom_schemes_manager")


def apply_target_finger_chain_detection_overrides(remap_mode: str) -> dict[str, Any]:
    normalized_mode = normalize_target_finger_chain_remap_mode(remap_mode)
    if normalized_mode in {"none", "off", "preserve"}:
        return {
            "applied": False,
            "mode": "none",
            "overrideCount": 0,
            "overrides": [],
        }

    if normalized_mode != "citizen_012":
        raise RuntimeError(f"Unsupported target finger chain remap mode '{remap_mode}'.")

    detection_manager = load_rokoko_detection_manager_module()
    custom_list = getattr(detection_manager, "bone_detection_list_custom", None)
    if not isinstance(custom_list, dict):
        return {
            "applied": False,
            "mode": normalized_mode,
            "overrideCount": 0,
            "overrides": [],
            "reason": "rokoko_detection_manager_has_no_custom_list",
        }

    overrides: list[dict[str, Any]] = []
    for bone_key, desired_targets in TARGET_FINGER_CHAIN_CITIZEN_012_DETECTION_OVERRIDES.items():
        current_targets = list(custom_list.get(bone_key) or [])
        if current_targets == desired_targets:
            continue
        custom_list[bone_key] = list(desired_targets)
        overrides.append(
            {
                "boneKey": bone_key,
                "before": current_targets,
                "after": list(desired_targets),
            }
        )

    combine_lists = getattr(detection_manager, "combine_lists", None)
    base_detection_list = getattr(detection_manager, "bone_detection_list_unmodified", None)
    if callable(combine_lists) and isinstance(base_detection_list, dict):
        detection_manager.bone_detection_list = combine_lists(base_detection_list, custom_list)

    return {
        "applied": bool(overrides),
        "mode": normalized_mode,
        "overrideCount": len(overrides),
        "overrides": overrides,
        "notes": [
            "Patched Rokoko detection aliases in memory so Citizen finger chains skip meta bones for UAL2-style hands.",
            "This leaves the addon's global custom_bone_list.json untouched and only affects the current headless run.",
        ],
    }


def apply_target_finger_chain_remap(
    scene: Any,
    target_armature: Any,
    remap_mode: str,
) -> dict[str, Any]:
    normalized_mode = normalize_target_finger_chain_remap_mode(remap_mode)
    if normalized_mode in {"none", "off", "preserve"}:
        return {
            "applied": False,
            "mode": "none",
            "overrideCount": 0,
            "overrides": [],
        }

    if normalized_mode != "citizen_012":
        raise RuntimeError(f"Unsupported target finger chain remap mode '{remap_mode}'.")

    bone_list = getattr(scene, "rsl_retargeting_bone_list", None)
    if bone_list is None:
        return {
            "applied": False,
            "mode": normalized_mode,
            "overrideCount": 0,
            "overrides": [],
            "reason": "scene_has_no_bone_list",
        }

    target_bone_names = set(target_armature.pose.bones.keys())
    overrides: list[dict[str, str]] = []

    for item in bone_list:
        bone_key = str(getattr(item, "bone_name_key", "") or "").strip()
        target_override = TARGET_FINGER_CHAIN_CITIZEN_012_KEY_TO_TARGET.get(bone_key)
        if not target_override or target_override not in target_bone_names:
            continue

        target_before = str(getattr(item, "bone_name_target", "") or "").strip()
        if target_before == target_override:
            continue

        overrides.append(
            {
                "boneKey": bone_key,
                "sourceBone": str(getattr(item, "bone_name_source", "") or "").strip(),
                "targetBefore": target_before,
                "targetAfter": target_override,
            }
        )
        item.bone_name_target = target_override

    return {
        "applied": bool(overrides),
        "mode": normalized_mode,
        "overrideCount": len(overrides),
        "overrides": overrides,
        "notes": [
            "Remapped Rokoko finger chains to Citizen 0/1/2 segments so source proximal bones skip Citizen meta finger bones.",
        ],
    }


def filter_bone_map_for_target_finger_chain_remap(
    required_bone_map: dict[str, dict[str, str]],
    remap_mode: str,
) -> tuple[dict[str, dict[str, str]], list[str]]:
    normalized_mode = normalize_target_finger_chain_remap_mode(remap_mode)
    if normalized_mode != "citizen_012":
        return dict(required_bone_map), []

    filtered_map = {
        slot: mapping
        for slot, mapping in required_bone_map.items()
        if slot not in TARGET_FINGER_CHAIN_CITIZEN_012_SLOT_IDS
    }
    skipped_slots = sorted(
        slot
        for slot in required_bone_map.keys()
        if slot in TARGET_FINGER_CHAIN_CITIZEN_012_SLOT_IDS
    )
    return filtered_map, skipped_slots


def apply_required_rokoko_bone_map_overrides(
    scene: Any,
    required_bone_map: dict[str, dict[str, str]],
    source_armature: Any,
    target_armature: Any,
) -> dict[str, Any]:
    bone_list = getattr(scene, "rsl_retargeting_bone_list", None)
    if bone_list is None:
        return {
            "enabled": False,
            "applied": False,
            "reason": "scene_has_no_bone_list",
        }

    validated_pairs: list[dict[str, str]] = []
    missing_source_bones: list[str] = []
    missing_target_bones: list[str] = []
    for slot, mapping in sorted(required_bone_map.items()):
        source_bone = str(mapping.get("sourceBone") or "").strip()
        target_bone = str(mapping.get("targetBone") or "").strip()
        if not source_bone or not target_bone:
            continue
        if source_armature.pose.bones.get(source_bone) is None:
            missing_source_bones.append(source_bone)
            continue
        if target_armature.pose.bones.get(target_bone) is None:
            missing_target_bones.append(target_bone)
            continue
        validated_pairs.append(
            {
                "slot": slot,
                "sourceBone": source_bone,
                "targetBone": target_bone,
            }
        )

    cleared_conflicts: list[dict[str, str]] = []
    updated_entries: list[dict[str, str]] = []
    added_entries: list[dict[str, str]] = []
    for pair in validated_pairs:
        slot = pair["slot"]
        source_bone = pair["sourceBone"]
        target_bone = pair["targetBone"]

        for item in bone_list:
            item_source = str(getattr(item, "bone_name_source", "") or "").strip()
            item_target = str(getattr(item, "bone_name_target", "") or "").strip()
            if item_target == target_bone and item_source != source_bone:
                item.bone_name_target = ""
                cleared_conflicts.append(
                    {
                        "slot": slot,
                        "sourceBone": item_source,
                        "clearedTargetBone": target_bone,
                    }
                )

        matching_item = next(
            (
                item
                for item in bone_list
                if str(getattr(item, "bone_name_source", "") or "").strip() == source_bone
            ),
            None,
        )
        if matching_item is None:
            matching_item = bone_list.add()
            matching_item.is_custom = True
            matching_item.bone_name_source = source_bone
            matching_item.bone_name_key = f"required_slot_{slot}"
            added_entries.append(
                {
                    "slot": slot,
                    "sourceBone": source_bone,
                    "targetBone": target_bone,
                }
            )

        current_target = str(getattr(matching_item, "bone_name_target", "") or "").strip()
        if current_target != target_bone:
            matching_item.bone_name_target = target_bone
            updated_entries.append(
                {
                    "slot": slot,
                    "sourceBone": source_bone,
                    "fromTargetBone": current_target or None,
                    "toTargetBone": target_bone,
                }
            )

    return {
        "enabled": True,
        "applied": bool(cleared_conflicts or updated_entries or added_entries),
        "validatedPairCount": len(validated_pairs),
        "validatedPairs": validated_pairs,
        "missingSourceBones": sorted(set(missing_source_bones)),
        "missingTargetBones": sorted(set(missing_target_bones)),
        "clearedConflicts": cleared_conflicts,
        "updatedEntries": updated_entries,
        "addedEntries": added_entries,
    }


def normalize_action_name_for_backend(
    target_profile: dict[str, Any],
    source_leaf_action_name: str,
    backend_name: str,
) -> str:
    backend_suffix = sanitize_name(backend_name)
    return (
        f"RETARGET__{sanitize_name(target_profile.get('profileId', 'target'))}"
        f"__{sanitize_name(source_leaf_action_name)}"
        f"__{backend_suffix}"
    )


def duplicate_armature_object(armature: Any, name_suffix: str) -> Any:
    duplicate = armature.copy()
    duplicate.data = armature.data.copy()
    duplicate.name = f"{armature.name}{name_suffix}"
    duplicate.animation_data_clear()

    if armature.users_collection:
        for collection in armature.users_collection:
            collection.objects.link(duplicate)
    else:
        bpy.context.scene.collection.objects.link(duplicate)

    duplicate.hide_set(False)
    duplicate.hide_viewport = False
    duplicate.hide_render = True
    bpy.context.view_layer.update()
    return duplicate


def apply_current_pose_as_rest(armature: Any) -> None:
    try:
        bpy.ops.object.mode_set(mode="OBJECT")
    except Exception:
        pass
    bpy.ops.object.select_all(action="DESELECT")
    armature.hide_set(False)
    armature.select_set(True)
    bpy.context.view_layer.objects.active = armature
    bpy.ops.object.mode_set(mode="POSE")

    action_tmp = armature.animation_data.action if armature.animation_data else None
    slot_tmp = armature.animation_data.action_slot if armature.animation_data else None
    if armature.animation_data:
        armature.animation_data.action = None
    bpy.ops.pose.armature_apply()
    if action_tmp is not None:
        ensure_animation_data(armature).action = action_tmp
        if slot_tmp is not None:
            try:
                armature.animation_data.action_slot = slot_tmp
            except Exception:
                pass
    bpy.ops.object.mode_set(mode="OBJECT")
    bpy.context.view_layer.update()


def create_target_pose_proxy(target_armature: Any) -> Any:
    proxy = duplicate_armature_object(target_armature, "__pose_proxy")
    apply_current_pose_as_rest(proxy)
    return proxy


def create_source_pose_reference(
    source_armature: Any,
    source_pose_action: Any,
    source_pose_action_name: str,
) -> tuple[Any, Any]:
    reference = duplicate_armature_object(source_armature, "__pose_reference")
    helper_action = source_pose_action.copy()
    helper_action.name = f"{sanitize_name(source_pose_action_name)}__pose_reference"
    helper_action.use_fake_user = False
    assign_action_to_armature(reference, helper_action)
    return reference, helper_action


def create_source_rest_pose_reference(
    source_armature: Any,
    motion_action: Any,
    motion_action_name: str,
) -> tuple[Any, Any]:
    reference = duplicate_armature_object(source_armature, "__rest_pose_reference")
    reference.animation_data_clear()
    for pose_bone in reference.pose.bones:
        pose_bone.matrix_basis = Matrix.Identity(4)
    helper_action = motion_action.copy()
    helper_action.name = f"{sanitize_name(motion_action_name)}__rest_pose_motion"
    helper_action.use_fake_user = False
    assign_action_to_armature(reference, helper_action)
    bpy.context.view_layer.update()
    return reference, helper_action


def cleanup_armature_object(armature: Any) -> None:
    armature_data = armature.data
    bpy.data.objects.remove(armature, do_unlink=True)
    if armature_data.users == 0:
        bpy.data.armatures.remove(armature_data, do_unlink=True)


def cleanup_action(action: Any | None) -> None:
    if action is None:
        return
    if action.users == 0:
        bpy.data.actions.remove(action, do_unlink=True)


def create_export_armature_copy(
    armature: Any,
    action: Any,
    export_bone_names: list[str],
) -> Any:
    try:
        bpy.ops.object.mode_set(mode="OBJECT")
    except Exception:
        pass
    bpy.ops.object.select_all(action="DESELECT")
    armature.hide_set(False)
    armature.select_set(True)
    bpy.context.view_layer.objects.active = armature
    bpy.ops.object.duplicate(linked=False)
    export_armature = bpy.context.view_layer.objects.active
    export_armature.name = f"{armature.name}__animation_export"

    keep_bones = set(export_bone_names)
    if keep_bones:
        bpy.ops.object.mode_set(mode="EDIT")
        for edit_bone in list(export_armature.data.edit_bones):
            if edit_bone.name not in keep_bones:
                export_armature.data.edit_bones.remove(edit_bone)
        bpy.ops.object.mode_set(mode="OBJECT")

    ensure_animation_data(export_armature).action = action
    bpy.context.view_layer.update()
    return export_armature


def bake_matching_animation_from_proxy(
    source_proxy: Any,
    target_armature: Any,
    action_name: str,
    root_location_bones: list[str],
) -> Any:
    source_action = resolve_action(source_proxy, action_name)
    assign_action_to_armature(source_proxy, source_action)
    animated_bones = extract_animated_bone_names(source_action)

    bpy.ops.object.select_all(action="DESELECT")
    target_armature.hide_set(False)
    target_armature.select_set(True)
    bpy.context.view_layer.objects.active = target_armature
    bpy.ops.object.mode_set(mode="POSE")
    bpy.ops.pose.select_all(action="DESELECT")

    constraint_suffix = "__RETARGET_PROXY"
    selected_bones: list[str] = []
    for data_bone in target_armature.data.bones:
        data_bone.select = False
    for bone_name in animated_bones:
        source_bone = source_proxy.pose.bones.get(bone_name)
        target_bone = target_armature.pose.bones.get(bone_name)
        if source_bone is None or target_bone is None:
            continue

        rotation_constraint = target_bone.constraints.new("COPY_ROTATION")
        rotation_constraint.name = f"{rotation_constraint.name}{constraint_suffix}"
        rotation_constraint.target = source_proxy
        rotation_constraint.subtarget = bone_name

        if bone_name in root_location_bones:
            location_constraint = target_bone.constraints.new("COPY_LOCATION")
            location_constraint.name = f"{location_constraint.name}{constraint_suffix}"
            location_constraint.target = source_proxy
            location_constraint.subtarget = bone_name

        data_bone = target_armature.data.bones.get(bone_name)
        if data_bone is not None:
            data_bone.select = True
        selected_bones.append(bone_name)

    if not selected_bones:
        raise PipelineError("Could not find any matching animated bones while baking from the pose proxy.")

    frame_start, frame_end = [int(value) for value in source_action.frame_range]
    bpy.context.scene.frame_start = frame_start
    bpy.context.scene.frame_end = frame_end
    bpy.context.scene.frame_set(frame_start)

    bpy.ops.nla.bake(
        frame_start=frame_start,
        frame_end=frame_end,
        visual_keying=True,
        only_selected=True,
        use_current_action=False,
        bake_types={"POSE"},
    )

    baked_action = resolve_action(target_armature, None)

    for pose_bone in target_armature.pose.bones:
        for constraint in list(pose_bone.constraints):
            if constraint_suffix in constraint.name:
                pose_bone.constraints.remove(constraint)

    bpy.ops.object.mode_set(mode="OBJECT")
    bpy.context.view_layer.update()
    return baked_action


def parse_root_motion_mode_from_metadata(metadata: dict[str, Any]) -> str:
    """Returns keep | in_place | fully_in_place (matches CitizenRetargetRootMotionMode)."""
    raw = str(metadata.get("rootMotionMode") or "Keep").strip()
    key = "".join(ch for ch in raw if not ch.isspace()).lower()
    if key == "inplace":
        return "in_place"
    if key == "fullyinplace":
        return "fully_in_place"
    if key == "keep":
        return "keep"
    return "keep"


def apply_root_motion_adjustments_after_bake(
    armature: Any,
    action: Any,
    root_bone_name: str,
    policy: str,
    *,
    reference_frame: int = -1,
    vertical_nudge_z: float = 0.0,
) -> None:
    """Pelvis world adjustment after proxy bake: RootMotionMode + optional reference frame, Z nudge.

    - *keep*: only applies vertical_nudge_z (uniform shift, keeps jumps/spins).
    - *in_place*: locks world XY to reference frame; Z from clip + nudge.
    - *fully_in_place*: locks world XYZ to reference frame + nudge.
    """
    if policy == "keep" and abs(vertical_nudge_z) < 1e-12:
        return
    if not root_bone_name:
        return
    pose_bone = armature.pose.bones.get(root_bone_name)
    if pose_bone is None:
        return

    scene = bpy.context.scene
    anim_data = armature.animation_data
    if anim_data is None:
        return
    prev_action = anim_data.action
    anim_data.action = action

    frame_start, frame_end = int(action.frame_range[0]), int(action.frame_range[1])
    ref = frame_start if reference_frame < 0 else max(frame_start, min(frame_end, int(reference_frame)))
    scene.frame_set(ref)
    bpy.context.view_layer.update()
    rest_world = (armature.matrix_world @ pose_bone.matrix).translation.copy()
    nudge = Vector((0.0, 0.0, float(vertical_nudge_z)))

    for frame in range(frame_start, frame_end + 1):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        matrix_world = armature.matrix_world @ pose_bone.matrix
        world_loc = matrix_world.translation
        if policy == "keep":
            target_world = world_loc + nudge
        elif policy == "fully_in_place":
            target_world = rest_world + nudge
        elif policy == "in_place":
            target_world = Vector((rest_world.x, rest_world.y, world_loc.z)) + nudge
        else:
            continue
        delta = target_world - world_loc
        if delta.length < 1e-9:
            continue
        new_world_matrix = Matrix.Translation(delta) @ matrix_world
        armature_space = armature.matrix_world.inverted() @ new_world_matrix
        pose_bone.matrix = armature_space
        pose_bone.keyframe_insert(data_path="location", frame=frame)
        if pose_bone.rotation_mode == "QUATERNION":
            pose_bone.keyframe_insert(data_path="rotation_quaternion", frame=frame)
        elif pose_bone.rotation_mode == "AXIS_ANGLE":
            pose_bone.keyframe_insert(data_path="rotation_axis_angle", frame=frame)
        else:
            pose_bone.keyframe_insert(data_path="rotation_euler", frame=frame)

    anim_data.action = prev_action
    bpy.context.view_layer.update()


def load_rokoko_retargeting_module() -> Any:
    return import_rokoko_submodule("operators.retargeting")


def make_patched_rokoko_copy_rest_pose(
    motion_source_armature: Any,
    motion_action: Any,
    force_rest_reference: bool = False,
):
    def patched_copy_rest_pose(self, context, armature_source):
        context.scene.tool_settings.use_keyframe_insert_auto = False

        try:
            bpy.ops.object.mode_set(mode="OBJECT")
        except Exception:
            pass
        bpy.ops.object.select_all(action="DESELECT")
        armature_source.hide_set(False)
        armature_source.select_set(True)
        bpy.context.view_layer.objects.active = armature_source
        bpy.ops.object.mode_set(mode="OBJECT")

        source_action_tmp = armature_source.animation_data.action if armature_source.animation_data else None
        source_slot_tmp = armature_source.animation_data.action_slot if armature_source.animation_data else None
        if force_rest_reference:
            if armature_source.animation_data:
                armature_source.animation_data.action = None
            for pose_bone in armature_source.pose.bones:
                pose_bone.matrix_basis = Matrix.Identity(4)
            bpy.context.view_layer.update()

        bpy.ops.object.duplicate_move(
            OBJECT_OT_duplicate={"linked": False, "mode": "TRANSLATION"},
            TRANSFORM_OT_translate={
                "value": (0, 0, 0),
                "constraint_axis": (False, True, False),
                "mirror": False,
                "snap": False,
                "remove_on_cancel": False,
                "release_confirm": False,
            },
        )

        source_armature_copy = context.object
        source_armature_copy.name = armature_source.name + "_copy"

        if force_rest_reference and source_action_tmp is not None:
            ensure_animation_data(armature_source).action = source_action_tmp
            if source_slot_tmp is not None:
                try:
                    armature_source.animation_data.action_slot = source_slot_tmp
                except Exception:
                    pass
            bpy.context.view_layer.update()

        bpy.ops.object.select_all(action="DESELECT")
        source_armature_copy.hide_set(False)
        source_armature_copy.select_set(True)
        bpy.context.view_layer.objects.active = source_armature_copy
        bpy.ops.object.mode_set(mode="OBJECT")
        bpy.ops.object.mode_set(mode="POSE")

        action_tmp = source_armature_copy.animation_data.action if source_armature_copy.animation_data else None
        if source_armature_copy.animation_data:
            source_armature_copy.animation_data.action = None
        bpy.ops.pose.armature_apply()
        if action_tmp is not None:
            ensure_animation_data(source_armature_copy).action = action_tmp

        bpy.ops.object.mode_set(mode="OBJECT")

        motion_action_copy = motion_action.copy()
        motion_action_copy.name = f"{motion_action.name}__motion_driver"
        assign_action_to_armature(source_armature_copy, motion_action_copy)

        for bone in source_armature_copy.pose.bones:
            constraint = bone.constraints.new("COPY_TRANSFORMS")
            constraint.name = bone.name
            constraint.target = motion_source_armature
            constraint.subtarget = bone.name

        bpy.ops.object.mode_set(mode="OBJECT")
        return source_armature_copy

    return patched_copy_rest_pose


def run_rokoko_backend(
    context: RunContext,
    artifact_bundle: dict[str, Any],
    config: dict[str, Any],
) -> tuple[dict[str, Any], list[str], str]:
    source_armature = artifact_bundle.get("sourceArmature")
    target_armature = artifact_bundle.get("targetArmature")
    selected_action_name = artifact_bundle.get("selectedActionName")
    source_metrics = artifact_bundle.get("sourceMetrics") or {}
    required_bone_map = artifact_bundle.get("boneMap") or {}
    pose_normalization = dict(artifact_bundle.get("poseNormalization") or {})
    if source_armature is None or target_armature is None or not selected_action_name:
        raise PipelineError("Rokoko backend prerequisites are missing.")

    backend_settings = {
        "addonName": ROKOKO_ADDON_NAME,
        "autoScaling": bool(config.get("autoScaling", True)),
        "usePose": str(config.get("usePose", "REST")).strip().upper() or "REST",
        "useSourcePoseReference": bool(config.get("useSourcePoseReference", True)),
        "useTargetPoseProxy": bool(config.get("useTargetPoseProxy", True)),
        "skipCustomSchemeSave": bool(config.get("skipCustomSchemeSave", True)),
        "sourceObjectBasisMode": str(config.get("sourceObjectBasisMode", "match_target_rotation") or "").strip()
        or "match_target_rotation",
        "sourceObjectBasisToleranceDegrees": round_float(
            float(config.get("sourceObjectBasisToleranceDegrees", 0.5))
        ),
    }
    source_facing_euler_degrees = resolve_source_facing_euler_degrees(context, config)
    if any(abs(float(value)) > 0.001 for value in source_facing_euler_degrees):
        backend_settings["sourceFacingEulerDegrees"] = [round_float(float(value)) for value in source_facing_euler_degrees]
        config = dict(config)
        config["sourceFacingEulerDegrees"] = source_facing_euler_degrees
    backend_settings["targetPoseProxyMode"] = (
        "apply_as_rest_proxy"
        if pose_normalization
        and backend_settings["usePose"] == "CURRENT"
        and backend_settings["useTargetPoseProxy"]
        else "disabled"
    )
    if config.get("sourceObjectRotationEulerDegrees") is not None:
        backend_settings["sourceObjectRotationEulerDegrees"] = [
            round_float(float(value))
            for value in config.get("sourceObjectRotationEulerDegrees", [])
        ]

    def backend_callback() -> None:
        _, backend_version = enable_addon(ROKOKO_ADDON_NAME)
        backend_settings["addonName"] = get_rokoko_addon_module_name()
        artifact_bundle["backendVersion"] = backend_version

        source_action = resolve_action(source_armature, selected_action_name)
        assign_action_to_armature(source_armature, source_action)

        basis_normalization = normalize_source_armature_basis_for_backend(
            source_armature,
            target_armature,
            config,
        )
        artifact_bundle["backendBasisNormalization"] = basis_normalization
        if basis_normalization.get("applied"):
            before_rotation = basis_normalization.get("sourceRotationBeforeDegrees")
            after_rotation = basis_normalization.get("sourceRotationAfterDegrees")
            artifact_bundle["backendWarnings"] = dedupe_preserve_order(
                list(artifact_bundle.get("backendWarnings", []))
                + [
                    "Applied source armature basis normalization "
                    f"from {before_rotation} to {after_rotation} before Rokoko retarget."
                ]
            )

        scene = bpy.context.scene
        if pose_normalization and backend_settings["usePose"] == "CURRENT":
            scene.frame_set(int(pose_normalization.get("sourcePoseFrame", 0)))
            bpy.context.view_layer.update()

        operator_source_armature = source_armature
        source_pose_reference = None
        source_reference_action = None
        if (
            pose_normalization
            and backend_settings["usePose"] == "CURRENT"
            and backend_settings["useSourcePoseReference"]
        ):
            source_pose_mode = str(pose_normalization.get("sourcePoseMode") or "action_frame").strip().lower()
            if source_pose_mode == "rest_pose":
                source_pose_reference, source_reference_action = create_source_rest_pose_reference(
                    source_armature,
                    source_action,
                    selected_action_name,
                )
            else:
                source_pose_action = resolve_action(
                    source_armature,
                    str(pose_normalization.get("sourcePoseAction") or ""),
                )
                source_pose_reference, source_reference_action = create_source_pose_reference(
                    source_armature,
                    source_pose_action,
                    str(pose_normalization.get("sourcePoseAction") or "source_pose"),
                )
            operator_source_armature = source_pose_reference
            artifact_bundle["backendSourcePoseReference"] = {
                "name": source_pose_reference.name,
                "mode": "pose_reference_proxy",
                "sourcePoseMode": source_pose_mode,
                "sourcePoseAction": pose_normalization.get("sourcePoseAction"),
                "sourcePoseFrame": pose_normalization.get("sourcePoseFrame"),
            }

        operator_target_armature = target_armature
        target_pose_proxy = None
        if pose_normalization and backend_settings["targetPoseProxyMode"] == "apply_as_rest_proxy":
            target_pose_proxy = create_target_pose_proxy(target_armature)
            operator_target_armature = target_pose_proxy
            artifact_bundle["backendTargetPoseProxy"] = {
                "name": target_pose_proxy.name,
                "sourcePresetId": pose_normalization.get("targetPosePresetId"),
                "mode": backend_settings["targetPoseProxyMode"],
            }

        scene.rsl_retargeting_armature_source = operator_source_armature
        scene.rsl_retargeting_armature_target = operator_target_armature
        scene.rsl_retargeting_auto_scaling = backend_settings["autoScaling"]
        scene.rsl_retargeting_use_pose = backend_settings["usePose"]

        bpy.ops.object.select_all(action="DESELECT")
        operator_source_armature.hide_set(False)
        operator_target_armature.hide_set(False)
        bpy.context.view_layer.objects.active = operator_target_armature
        bpy.ops.object.mode_set(mode="OBJECT")

        target_finger_chain_remap_mode = resolve_target_finger_chain_remap_mode(context, config)
        target_finger_chain_detection_override = apply_target_finger_chain_detection_overrides(
            target_finger_chain_remap_mode
        )
        artifact_bundle["backendTargetFingerChainDetectionOverride"] = target_finger_chain_detection_override

        build_result = bpy.ops.rsl.build_bone_list()
        if "FINISHED" not in build_result:
            raise PipelineError(f"Rokoko build bone list returned {build_result}.")

        target_finger_chain_remap_summary = apply_target_finger_chain_remap(
            scene,
            operator_target_armature,
            target_finger_chain_remap_mode,
        )
        artifact_bundle["backendTargetFingerChainRemap"] = target_finger_chain_remap_summary

        should_apply_bone_map_overrides = bool(config.get("applyRequiredBoneMapOverrides", False)) or bool(
            context.bone_map_overrides.get("slots")
        )
        mapping_override_summary = {
            "enabled": False,
            "applied": False,
            "reason": "disabled_by_config",
        }
        if should_apply_bone_map_overrides:
            filtered_required_bone_map, skipped_slots = filter_bone_map_for_target_finger_chain_remap(
                required_bone_map,
                target_finger_chain_remap_mode,
            )
            mapping_override_summary = apply_required_rokoko_bone_map_overrides(
                scene,
                filtered_required_bone_map,
                operator_source_armature,
                operator_target_armature,
            )
            if skipped_slots:
                mapping_override_summary["skippedSlots"] = skipped_slots
                mapping_override_summary["skipReason"] = (
                    f"target_finger_chain_remap_mode={target_finger_chain_remap_mode}"
                )
        artifact_bundle["backendMappingOverrideSummary"] = mapping_override_summary

        generated_bone_map, mapping_warnings = collect_rokoko_generated_bone_map(scene, required_bone_map)
        artifact_bundle["backendGeneratedBoneMap"] = generated_bone_map
        artifact_bundle["backendWarnings"] = dedupe_preserve_order(
            list(artifact_bundle.get("backendWarnings", [])) + mapping_warnings
        )

        retargeting_module = load_rokoko_retargeting_module()
        custom_schemes_manager = load_rokoko_custom_schemes_manager_module()
        original_copy_rest_pose = retargeting_module.RetargetAnimation.copy_rest_pose
        original_save_retargeting_to_list = custom_schemes_manager.save_retargeting_to_list
        if source_pose_reference is not None:
            retargeting_module.RetargetAnimation.copy_rest_pose = make_patched_rokoko_copy_rest_pose(
                source_armature,
                source_action,
                force_rest_reference=source_pose_mode == "rest_pose",
            )
        if backend_settings["skipCustomSchemeSave"]:
            def skip_rokoko_custom_scheme_save() -> None:
                bone_list = list(getattr(bpy.context.scene, "rsl_retargeting_bone_list", []) or [])
                artifact_bundle["backendCustomSchemeSave"] = {
                    "skipped": True,
                    "reason": "headless_run_does_not_persist_rokoko_custom_bone_schemes",
                    "boneListCount": len(bone_list),
                    "customEntryCount": sum(
                        1
                        for item in bone_list
                        if bool(getattr(item, "is_custom", False))
                        and str(getattr(item, "bone_name_source", "") or "").strip()
                        and str(getattr(item, "bone_name_target", "") or "").strip()
                    ),
                }

            custom_schemes_manager.save_retargeting_to_list = skip_rokoko_custom_scheme_save
        else:
            artifact_bundle["backendCustomSchemeSave"] = {
                "skipped": False,
                "reason": "enabled_by_backend_config",
            }
        try:
            retarget_result = bpy.ops.rsl.retarget_animation()
        finally:
            retargeting_module.RetargetAnimation.copy_rest_pose = original_copy_rest_pose
            custom_schemes_manager.save_retargeting_to_list = original_save_retargeting_to_list
        if "FINISHED" not in retarget_result:
            raise PipelineError(f"Rokoko retarget animation returned {retarget_result}.")

        pose_restore_runtime = artifact_bundle.get("poseRestoreRuntime") or {}
        pose_restore_status = "not_requested"
        if pose_restore_runtime:
            restore_warnings: list[str] = []
            source_missing_restore = restore_pose_basis_state(
                source_armature,
                pose_restore_runtime.get("sourceOriginalBasis") or {},
            )
            target_missing_restore = restore_pose_basis_state(
                target_armature,
                pose_restore_runtime.get("targetOriginalBasis") or {},
            )
            if source_missing_restore:
                restore_warnings.append(
                    "Could not fully restore source pose basis after Rokoko retarget: "
                    f"{sorted(source_missing_restore)}"
                )
            if target_missing_restore:
                restore_warnings.append(
                    "Could not fully restore target pose basis after Rokoko retarget: "
                    f"{sorted(target_missing_restore)}"
                )

            scene.frame_set(int(pose_restore_runtime.get("sceneFrameBeforeNormalization", scene.frame_current)))
            assign_action_to_armature(source_armature, source_action)
            bpy.context.view_layer.update()

            pose_restore_status = "restored" if not restore_warnings else "partial"
            if restore_warnings:
                artifact_bundle["backendWarnings"] = dedupe_preserve_order(
                    list(artifact_bundle.get("backendWarnings", [])) + restore_warnings
                )

        if source_pose_reference is not None:
            cleanup_armature_object(source_pose_reference)
            cleanup_action(source_reference_action)
            bpy.context.view_layer.update()

        if target_pose_proxy is not None:
            proxy_action = resolve_action(target_pose_proxy, None)
            pelvis_mapping = required_bone_map.get("pelvis") or {}
            root_location_bones = []
            default_root_bone = str(
                pelvis_mapping.get("targetBone")
                or context.target_profile.get("defaultRootBone")
                or ""
            ).strip()
            if default_root_bone and target_armature.pose.bones.get(default_root_bone) is not None:
                root_location_bones.append(default_root_bone)

            baked_target_action = bake_matching_animation_from_proxy(
                target_pose_proxy,
                target_armature,
                proxy_action.name,
                root_location_bones,
            )
            override_metadata = dict((context.bone_map_overrides or {}).get("metadata") or {})
            root_policy = parse_root_motion_mode_from_metadata(override_metadata)
            try:
                ref_frame = int(override_metadata.get("rootMotionReferenceFrame", -1))
            except (TypeError, ValueError):
                ref_frame = -1
            try:
                vert_nudge = float(override_metadata.get("rootMotionVerticalNudge", 0.0))
            except (TypeError, ValueError):
                vert_nudge = 0.0
            if default_root_bone and (root_policy != "keep" or abs(vert_nudge) > 1e-12):
                apply_root_motion_adjustments_after_bake(
                    target_armature,
                    baked_target_action,
                    default_root_bone,
                    root_policy,
                    reference_frame=ref_frame,
                    vertical_nudge_z=vert_nudge,
                )
            artifact_bundle["backendTargetPoseProxy"] = {
                **dict(artifact_bundle.get("backendTargetPoseProxy") or {}),
                "proxyActionName": proxy_action.name,
                "bakedTargetActionName": baked_target_action.name,
                "rootLocationBones": root_location_bones,
                "appliedRootMotionPolicy": root_policy,
                "rootMotionReferenceFrame": ref_frame,
                "rootMotionVerticalNudge": vert_nudge,
            }

            if proxy_action.users == 0:
                bpy.data.actions.remove(proxy_action, do_unlink=True)
            cleanup_armature_object(target_pose_proxy)
            bpy.context.view_layer.update()

        if pose_normalization:
            pose_normalization["poseRestoreStatus"] = pose_restore_status
            artifact_bundle["poseNormalization"] = pose_normalization

    log_text, operator_warnings = capture_operator_output(backend_callback)
    backend_warnings = dedupe_preserve_order(
        operator_warnings + list(artifact_bundle.get("backendWarnings", []))
    )

    target_action = resolve_action(target_armature, None)
    source_leaf_action_name = (
        source_metrics.get("selectedAction", {}) or {}
    ).get("leafName") or action_leaf_name(target_action.name)
    normalized_action_name = normalize_action_name_for_backend(
        context.target_profile,
        str(source_leaf_action_name),
        "rokoko_operator",
    )
    target_action.name = normalized_action_name
    assign_action_to_armature(target_armature, target_action)

    backend_metrics = {
        "retargetBackend": "rokoko_operator",
        "backendVersion": artifact_bundle.get("backendVersion"),
        "backendSettings": backend_settings,
        "basisNormalization": artifact_bundle.get("backendBasisNormalization"),
        "poseNormalization": artifact_bundle.get("poseNormalization"),
        "sourcePoseReference": artifact_bundle.get("backendSourcePoseReference"),
        "targetPoseProxy": artifact_bundle.get("backendTargetPoseProxy"),
        "customSchemeSave": artifact_bundle.get("backendCustomSchemeSave"),
        "backendMappingOverrides": artifact_bundle.get("backendMappingOverrideSummary"),
        "backendGeneratedBoneMap": artifact_bundle.get("backendGeneratedBoneMap", []),
        "boneListCount": len(artifact_bundle.get("backendGeneratedBoneMap", [])),
        "mappedBoneCount": len(
            [entry for entry in artifact_bundle.get("backendGeneratedBoneMap", []) if entry.get("targetBone")]
        ),
        "unmappedBoneCount": len(
            [entry for entry in artifact_bundle.get("backendGeneratedBoneMap", []) if not entry.get("targetBone")]
        ),
        "targetActionName": target_action.name,
        "frameRange": [int(value) for value in target_action.frame_range],
        "warningCount": len(backend_warnings),
    }
    return backend_metrics, backend_warnings, log_text


def inspect_fbx_file(
    path: Path,
    profile: dict[str, Any],
    selected_action_name: str | None,
    import_settings: dict[str, Any],
) -> tuple[dict[str, Any], str, list[str]]:
    reset_scene()

    def importer() -> None:
        bpy.ops.import_scene.fbx(
            filepath=str(path),
            **import_settings,
        )

    log_text, warnings = capture_operator_output(importer)
    metrics = collect_scene_metrics(profile, selected_action_name)
    return metrics, log_text, warnings


def maybe_save_snapshot(context: RunContext, name: str, notes: list[str]) -> str | None:
    if not (context.debug_enabled and context.save_intermediate_blend_files):
        return None
    snapshot_path = context.snapshots_dir / f"{sanitize_name(name)}.blend"
    ensure_dir(snapshot_path.parent)
    bpy.ops.wm.save_as_mainfile(filepath=str(snapshot_path), copy=True)
    notes.append(f"Saved snapshot {snapshot_path}")
    return str(snapshot_path)


def summarize_metrics(metrics: dict[str, Any] | None) -> dict[str, Any] | None:
    if not metrics:
        return None
    selected_action = metrics.get("selectedAction") or {}
    return {
        "armatureName": metrics.get("armatureName"),
        "boneCount": metrics.get("boneCount"),
        "actionCount": metrics.get("actionCount"),
        "selectedAction": (
            {
                "name": selected_action.get("name"),
                "leafName": selected_action.get("leafName"),
                "frameRange": selected_action.get("frameRange"),
                "animatedBoneCount": selected_action.get("animatedBoneCount"),
                "rootBoneName": selected_action.get("rootBoneName"),
                "rootDisplacement": selected_action.get("rootDisplacement"),
                "rootPathLength": selected_action.get("rootPathLength"),
            }
            if selected_action
            else None
        ),
    }


def compare_metrics(
    source_metrics: dict[str, Any],
    export_metrics: dict[str, Any],
    config: dict[str, Any],
) -> tuple[list[str], list[str], dict[str, Any]]:
    failures: list[str] = []
    warnings: list[str] = []

    source_bones = source_metrics["boneNames"]
    export_bones = export_metrics["boneNames"]
    source_action = source_metrics.get("selectedAction")
    export_action = export_metrics.get("selectedAction")

    if source_action is None or export_action is None:
        failures.append("Source or export metrics are missing a selected action summary.")
        return failures, warnings, {
            "missingBones": [],
            "extraBones": [],
            "missingAnimatedBones": [],
            "extraAnimatedBones": [],
            "extraLeafBones": [],
            "rootEndpointDrift": None,
            "rootPathLengthDelta": None,
            "rootDriftThreshold": None,
        }

    missing_bones = []
    extra_bones = []
    exact_bone_count = bool(config.get("exactBoneCount", config.get("compareBoneCount", True)))
    exact_bone_name_set = bool(config.get("exactBoneNameSet", config.get("compareBoneNames", True)))
    exact_frame_range = bool(config.get("exactFrameRange", config.get("compareFrameRange", True)))
    exact_animated_bone_set = bool(
        config.get("exactAnimatedBoneSet", config.get("compareAnimatedBoneSet", True))
    )
    check_leaf_bone_injection = bool(config.get("checkLeafBoneInjection", config.get("failOnLeafBones", True)))
    check_root_path_drift = bool(config.get("checkRootPathDrift", config.get("compareRootMotion", True)))

    if exact_bone_count and source_metrics["boneCount"] != export_metrics["boneCount"]:
        failures.append(
            f"Bone count changed from {source_metrics['boneCount']} to {export_metrics['boneCount']}."
        )

    if exact_bone_name_set:
        missing_bones = sorted(set(source_bones) - set(export_bones))
        extra_bones = sorted(set(export_bones) - set(source_bones))
        if missing_bones:
            failures.append(f"Missing exported bones: {missing_bones}")
        if extra_bones:
            failures.append(f"Unexpected exported bones: {extra_bones}")

    if exact_frame_range and source_action["frameRange"] != export_action["frameRange"]:
        failures.append(
            f"Frame range changed from {source_action['frameRange']} to {export_action['frameRange']}."
        )

    source_animated = set(source_action["animatedBoneNames"])
    export_animated = set(export_action["animatedBoneNames"])
    missing_animated = sorted(source_animated - export_animated)
    extra_animated = sorted(export_animated - source_animated)
    if exact_animated_bone_set:
        if missing_animated:
            failures.append(f"Missing animated bones after export: {missing_animated}")
        if extra_animated:
            failures.append(f"Unexpected animated bones after export: {extra_animated}")
    else:
        if missing_animated:
            warnings.append(f"Animated bone set shrank: {missing_animated}")

    extra_leaf_bones = []
    if check_leaf_bone_injection:
        source_leaf = set(source_metrics["leafBones"])
        export_leaf = set(export_metrics["leafBones"])
        extra_leaf_bones = sorted(export_leaf - source_leaf)
        if extra_leaf_bones:
            failures.append(f"Exporter injected leaf bones: {extra_leaf_bones}")

    source_root_path = float(source_action.get("rootPathLength") or 0.0)
    export_root_path = float(export_action.get("rootPathLength") or 0.0)
    path_length_delta = abs(source_root_path - export_root_path)
    endpoint_drift = vector_distance(
        source_action.get("rootEndLocation"),
        export_action.get("rootEndLocation"),
    )

    absolute_tolerance = float(config.get("rootDriftAbsoluteTolerance", config.get("rootMotionToleranceAbsolute", 0.01)))
    relative_tolerance = float(config.get("rootDriftRelativeTolerance", config.get("rootMotionToleranceRelative", 0.001)))
    drift_threshold = max(absolute_tolerance, relative_tolerance * source_root_path)

    if check_root_path_drift:
        if endpoint_drift is not None and endpoint_drift > drift_threshold:
            failures.append(
                f"Root endpoint drift {round_float(endpoint_drift)} exceeded tolerance {round_float(drift_threshold)}."
            )
        if path_length_delta > drift_threshold:
            failures.append(
                f"Root path length delta {round_float(path_length_delta)} exceeded tolerance {round_float(drift_threshold)}."
            )

    comparison = {
        "missingBones": missing_bones,
        "extraBones": extra_bones,
        "missingAnimatedBones": missing_animated,
        "extraAnimatedBones": extra_animated,
        "extraLeafBones": extra_leaf_bones,
        "rootEndpointDrift": round_float(endpoint_drift),
        "rootPathLengthDelta": round_float(path_length_delta),
        "rootDriftThreshold": round_float(drift_threshold),
    }
    return failures, warnings, comparison


def persist_stage_artifacts(
    context: RunContext,
    stage_result: StageResult,
    artifact_bundle: dict[str, Any],
) -> None:
    if context.save_stage_metrics and stage_result.metrics:
        metrics_path = context.metrics_dir / f"{sanitize_name(stage_result.stage_id)}.json"
        write_json(metrics_path, stage_result.metrics)
        stage_result.outputs["metricsPath"] = str(metrics_path)

    log_text = artifact_bundle.get("_stage_log_text")
    if log_text:
        log_path = context.logs_dir / f"{sanitize_name(stage_result.stage_id)}.log"
        write_text(log_path, log_text)
        stage_result.outputs["logPath"] = str(log_path)


def mark_skipped(stage_id: str, enabled: bool, reason: str) -> StageResult:
    timestamp = utc_now()
    return StageResult(
        stage_id=stage_id,
        enabled=enabled,
        status="skipped",
        started_at=timestamp,
        finished_at=timestamp,
        debug_notes=[reason],
    )


class ImportFbxStage(Stage):
    stage_id = "import_fbx"

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        started_at = utc_now()
        reset_scene()
        import_settings = build_import_settings(config)

        def importer() -> None:
            bpy.ops.import_scene.fbx(
                filepath=str(context.input_path),
                **import_settings,
            )

        log_text, warnings = capture_operator_output(importer)
        armature = require_single_armature()

        notes = []
        snapshot = maybe_save_snapshot(context, "after_import", notes)

        artifact_bundle["importConfig"] = {
            "automaticBoneOrientation": bool(config.get("automaticBoneOrientation", False)),
            "animOffset": float(config.get("animOffset", 1.0)),
        }
        artifact_bundle["importWarnings"] = warnings
        artifact_bundle["_stage_log_text"] = log_text
        artifact_bundle["armatureName"] = armature.name
        artifact_bundle["sourceArmature"] = armature

        metrics = {
            "inputPath": str(context.input_path),
            "objectCount": len(bpy.data.objects),
            "armatureCount": len(find_armatures()),
            "importConfig": artifact_bundle["importConfig"],
        }
        if snapshot:
            metrics["snapshotPath"] = snapshot

        return StageResult(
            stage_id=self.stage_id,
            enabled=True,
            status="completed",
            started_at=started_at,
            finished_at=utc_now(),
            warnings=warnings,
            metrics=metrics,
            debug_notes=notes,
        )


class CollectSourceMetadataStage(Stage):
    stage_id = "collect_source_metadata"

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        started_at = utc_now()
        source_armature = require_single_armature()
        source_preparation = apply_source_preparation(
            source_armature,
            str(config.get("sourcePreparationMode", "none") or "none"),
        )
        metrics = collect_armature_metrics(
            source_armature,
            context.source_profile,
            context.selected_action_name,
            allow_missing_action=False,
        )
        metrics["sourcePreparation"] = source_preparation
        artifact_bundle["sourcePreparation"] = source_preparation
        artifact_bundle["sourceMetrics"] = metrics
        artifact_bundle["selectedActionName"] = metrics["selectedAction"]["name"]
        artifact_bundle["_stage_log_text"] = ""
        return StageResult(
            stage_id=self.stage_id,
            enabled=True,
            status="completed",
            started_at=started_at,
            finished_at=utc_now(),
            metrics=metrics,
        )


class ValidateImportStage(Stage):
    stage_id = "validate_import"

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        started_at = utc_now()
        source_metrics = artifact_bundle.get("sourceMetrics")
        if not source_metrics:
            raise PipelineError("Source metrics are missing.")

        failures: list[str] = []
        warnings: list[str] = list(artifact_bundle.get("importWarnings", []))

        if config.get("requireSingleArmature", True) and source_metrics["armatureCount"] != 1:
            failures.append(f"Expected one armature, found {source_metrics['armatureCount']}.")
        if config.get("requireSelectedAction", True) and not source_metrics["selectedAction"]["name"]:
            failures.append("No selected action was resolved.")
        if config.get("requireRootBone", True) and not source_metrics["selectedAction"]["rootBoneName"]:
            failures.append(
                f"No root bone was resolved from candidates {context.source_profile.get('rootBoneCandidates', [])}."
            )

        notes = []
        snapshot = maybe_save_snapshot(context, "after_validate_import", notes)

        metrics = {
            "summary": summarize_metrics(source_metrics),
            "failureCount": len(failures),
        }
        if snapshot:
            metrics["snapshotPath"] = snapshot

        return StageResult(
            stage_id=self.stage_id,
            enabled=True,
            status="failed" if failures else "completed",
            started_at=started_at,
            finished_at=utc_now(),
            warnings=warnings,
            metrics=metrics,
            debug_notes=notes,
            error="\n".join(failures) if failures else None,
        )


class LoadTargetReferenceStage(Stage):
    stage_id = "load_target_reference"

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        started_at = utc_now()
        configured_path = str(config.get("targetReferencePath") or "").strip()
        profile_path = str(context.target_profile.get("sourceFiles", {}).get("citizenRefFbx", "")).strip()
        target_reference_path = Path(configured_path or profile_path).resolve()
        if not target_reference_path.is_file():
            raise PipelineError(f"Target reference FBX not found: {target_reference_path}")

        existing_armatures = {obj.as_pointer() for obj in find_armatures()}
        import_settings = build_import_settings({**artifact_bundle.get("importConfig", {}), **config})

        def importer() -> None:
            bpy.ops.import_scene.fbx(
                filepath=str(target_reference_path),
                **import_settings,
            )

        log_text, warnings = capture_operator_output(importer)
        new_armatures = [obj for obj in find_armatures() if obj.as_pointer() not in existing_armatures]
        if len(new_armatures) != 1:
            raise PipelineError(
                f"Expected exactly one new target armature, found {len(new_armatures)}."
            )

        target_armature = new_armatures[0]
        artifact_bundle["targetReferencePath"] = str(target_reference_path)
        artifact_bundle["targetArmature"] = target_armature
        artifact_bundle["targetArmatureName"] = target_armature.name
        artifact_bundle["targetImportWarnings"] = warnings
        artifact_bundle["_stage_log_text"] = log_text

        notes = []
        snapshot = maybe_save_snapshot(context, "after_load_target_reference", notes)
        metrics = {
            "targetReferencePath": str(target_reference_path),
            "targetArmatureName": target_armature.name,
            "targetBoneCount": len(target_armature.data.bones),
            "targetImportConfig": {
                "automaticBoneOrientation": bool(import_settings.get("automatic_bone_orientation", False)),
                "animOffset": float(import_settings.get("anim_offset", 1.0)),
            },
        }
        if snapshot:
            metrics["snapshotPath"] = snapshot

        return StageResult(
            stage_id=self.stage_id,
            enabled=True,
            status="completed",
            started_at=started_at,
            finished_at=utc_now(),
            warnings=warnings,
            metrics=metrics,
            debug_notes=notes,
        )


class CollectTargetMetadataStage(Stage):
    stage_id = "collect_target_metadata"

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        del config
        started_at = utc_now()
        target_armature = artifact_bundle.get("targetArmature")
        if target_armature is None:
            raise PipelineError("Target armature is missing.")

        metrics = collect_armature_metrics(
            target_armature,
            context.target_profile,
            selected_action_name=None,
            allow_missing_action=True,
        )
        artifact_bundle["targetMetrics"] = metrics
        artifact_bundle["_stage_log_text"] = ""
        return StageResult(
            stage_id=self.stage_id,
            enabled=True,
            status="completed",
            started_at=started_at,
            finished_at=utc_now(),
            warnings=list(artifact_bundle.get("targetImportWarnings", [])),
            metrics=metrics,
        )


class AlignRestPoseStage(Stage):
    stage_id = "align_rest_pose"

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        del config
        started_at = utc_now()
        source_armature = artifact_bundle.get("sourceArmature")
        target_armature = artifact_bundle.get("targetArmature")
        selected_action_name = artifact_bundle.get("selectedActionName")
        if source_armature is None or target_armature is None or not selected_action_name:
            raise PipelineError("Source armature, target armature or selected action is missing.")

        rest_action_name = str(context.source_profile.get("expectedRestPoseAction") or "").strip()
        selected_action = resolve_action(source_armature, selected_action_name)
        previous_frame = bpy.context.scene.frame_current

        rest_reference_mode = "bind_rest_pose"
        rest_action = None
        rest_frame = 0
        if rest_action_name:
            try:
                rest_action = resolve_action(source_armature, rest_action_name)
            except PipelineError:
                rest_action = None

        if rest_action is not None:
            rest_reference_mode = "source_rest_action"
            assign_action_to_armature(source_armature, rest_action)
            rest_frame = int(rest_action.frame_range[0])
            bpy.context.scene.frame_set(rest_frame)
            bpy.context.view_layer.update()

        slot_references_runtime: dict[str, dict[str, Any]] = {}
        slot_references_manifest: dict[str, dict[str, Any]] = {}
        failures: list[str] = []

        candidate_slots = sorted(
            set(context.source_profile.get("canonicalMapping", {}).keys())
            | set(context.source_profile.get("optionalCanonicalMapping", {}).keys())
            | set(context.target_profile.get("canonicalSlots", {}).keys())
        )

        for slot in candidate_slots:
            source_bone_name = resolve_source_slot_bone_name(context.source_profile, source_armature, slot)
            target_bone_name = resolve_target_slot_bone_name(context.target_profile, target_armature, slot)
            if not source_bone_name or not target_bone_name:
                continue

            source_pose_bone = source_armature.pose.bones.get(source_bone_name)
            target_pose_bone = target_armature.pose.bones.get(target_bone_name)
            if source_pose_bone is None or target_pose_bone is None:
                failures.append(
                    f"Could not resolve pose bones for slot '{slot}' ({source_bone_name} -> {target_bone_name})."
                )
                continue

            source_reference_local = (
                local_pose_matrix(source_pose_bone)
                if rest_action is not None
                else bone_rest_local_matrix(source_pose_bone)
            )
            target_rest_local = target_pose_bone.bone.matrix_local.copy()
            converter = target_rest_local.inverted() @ source_reference_local

            slot_references_runtime[slot] = {
                "sourceBone": source_bone_name,
                "targetBone": target_bone_name,
                "sourceReferenceLocal": source_reference_local.copy(),
                "targetRestLocal": target_rest_local.copy(),
                "converter": converter.copy(),
            }
            slot_references_manifest[slot] = {
                "sourceBone": source_bone_name,
                "targetBone": target_bone_name,
                "sourceReferenceLocal": matrix_to_rows(source_reference_local),
                "targetRestLocal": matrix_to_rows(target_rest_local),
                "converter": matrix_to_rows(converter),
            }

        assign_action_to_armature(source_armature, selected_action)
        bpy.context.scene.frame_set(previous_frame)
        bpy.context.view_layer.update()

        artifact_bundle["restAlignmentRuntime"] = slot_references_runtime
        artifact_bundle["restAlignment"] = slot_references_manifest
        artifact_bundle["_stage_log_text"] = ""

        notes = []
        snapshot = maybe_save_snapshot(context, "after_align_rest_pose", notes)
        metrics = {
            "restReferenceMode": rest_reference_mode,
            "restActionName": rest_action.name if rest_action is not None else None,
            "restFrame": rest_frame,
            "resolvedSlotCount": len(slot_references_runtime),
            "resolvedSlots": {
                slot: {
                    "sourceBone": payload["sourceBone"],
                    "targetBone": payload["targetBone"],
                }
                for slot, payload in slot_references_manifest.items()
            },
            "failureCount": len(failures),
        }
        if snapshot:
            metrics["snapshotPath"] = snapshot

        return StageResult(
            stage_id=self.stage_id,
            enabled=True,
            status="failed" if failures else "completed",
            started_at=started_at,
            finished_at=utc_now(),
            metrics=metrics,
            debug_notes=notes,
            error="\n".join(failures) if failures else None,
        )


class BuildBoneMapStage(Stage):
    stage_id = "build_bone_map"

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        started_at = utc_now()
        rest_alignment_runtime = artifact_bundle.get("restAlignmentRuntime") or {}
        required_slots = [str(slot) for slot in config.get("requiredSlots", [])]
        failures: list[str] = []

        bone_map: dict[str, dict[str, str]] = {}
        for slot in required_slots:
            slot_payload = rest_alignment_runtime.get(slot)
            if not slot_payload:
                continue
            bone_map[slot] = {
                "sourceBone": slot_payload["sourceBone"],
                "targetBone": slot_payload["targetBone"],
                "required": True,
                "enabled": True,
                "userOverridden": False,
                "mappingOrigin": "rest_alignment",
            }

        external_override_bundle = context.bone_map_overrides or {}
        external_override_slots = dict(external_override_bundle.get("slots") or {})
        external_override_summary = dict(external_override_bundle.get("summary") or {})
        invalid_override_slots = list(external_override_summary.get("invalidSlots") or [])
        missing_override_targets = list(external_override_summary.get("missingTargetBoneSlots") or [])

        for slot in invalid_override_slots:
            failures.append(
                f"External bone-map override for slot '{slot}' is invalid because it does not provide a source bone."
            )
        for slot in missing_override_targets:
            failures.append(
                f"External bone-map override for slot '{slot}' could not resolve a target bone on the Citizen profile."
            )

        for slot, payload in external_override_slots.items():
            normalized_entry = {
                "sourceBone": payload["sourceBone"],
                "targetBone": payload["targetBone"],
                "required": bool(payload.get("required", False)),
                "enabled": bool(payload.get("enabled", True)),
                "locked": bool(payload.get("locked", False)),
                "userOverridden": bool(payload.get("userOverridden", True)),
                "group": payload.get("group"),
                "mappingOrigin": payload.get("mappingOrigin") or "external_override",
            }
            bone_map[slot] = normalized_entry

        required_slot_ids = sorted(
            set(required_slots)
            | {
                slot
                for slot, payload in bone_map.items()
                if bool(payload.get("required", False))
            }
        )
        mapped_required_slot_count = sum(1 for slot in required_slot_ids if slot in bone_map)
        optional_slot_ids = sorted(
            slot
            for slot, payload in bone_map.items()
            if slot not in required_slot_ids and not bool(payload.get("required", False))
        )
        mapped_optional_slot_count = len(optional_slot_ids)

        unmapped_required_slots = [slot for slot in required_slot_ids if slot not in bone_map]
        for slot in unmapped_required_slots:
            failures.append(f"Required slot '{slot}' is still missing after applying external overrides.")

        mapping_coverage = {
            "requiredSlotCount": len(required_slot_ids),
            "mappedRequiredSlotCount": mapped_required_slot_count,
            "optionalSlotCount": len(optional_slot_ids),
            "mappedOptionalSlotCount": mapped_optional_slot_count,
            "resolvedCandidateSlotCount": len(rest_alignment_runtime),
            "totalMappedSlotCount": len(bone_map),
            "userOverrideSlotCount": len(
                [slot for slot, payload in bone_map.items() if bool(payload.get("userOverridden", False))]
            ),
            "lockedSlotCount": len(
                [slot for slot, payload in bone_map.items() if bool(payload.get("locked", False))]
            ),
        }

        artifact_bundle["boneMap"] = bone_map
        artifact_bundle["mappingCoverage"] = mapping_coverage
        artifact_bundle["unmappedRequiredSlots"] = unmapped_required_slots
        artifact_bundle["boneMapOverrideSummary"] = external_override_summary
        artifact_bundle["_stage_log_text"] = ""

        return StageResult(
            stage_id=self.stage_id,
            enabled=True,
            status="failed" if failures else "completed",
            started_at=started_at,
            finished_at=utc_now(),
            metrics={
                "boneMap": bone_map,
                "mappingCoverage": mapping_coverage,
                "unmappedRequiredSlots": unmapped_required_slots,
                "boneMapOverrideSummary": external_override_summary,
            },
            error="\n".join(failures) if failures else None,
        )


class NormalizeRetargetPoseStage(Stage):
    stage_id = "normalize_retarget_pose"

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        started_at = utc_now()
        source_armature = artifact_bundle.get("sourceArmature")
        target_armature = artifact_bundle.get("targetArmature")
        selected_action_name = artifact_bundle.get("selectedActionName")
        source_metrics = artifact_bundle.get("sourceMetrics") or {}
        if source_armature is None or target_armature is None or not selected_action_name:
            raise PipelineError("Pose-normalization prerequisites are missing.")

        source_pose_spec = resolve_source_pose_spec(context.source_profile, config)
        if context.target_pose_preset_override:
            config = dict(config)
            config["targetPosePresetId"] = context.target_pose_preset_override
        target_pose_preset_id, target_pose_preset = resolve_target_pose_preset(
            context.target_profile,
            config,
        )
        target_rotation_format = target_pose_preset["rotationFormat"]
        source_selected_action = resolve_action(source_armature, selected_action_name)
        source_action_frame = int(
            (source_metrics.get("selectedAction") or {}).get("frameRange", [0])[0]
        )

        source_original_basis = capture_pose_basis_state(source_armature)
        target_original_basis = capture_pose_basis_state(target_armature)

        source_pose_action_name = str(source_pose_spec.get("actionName") or "").strip()
        source_pose_action = None
        if source_pose_spec["mode"] == "rest_pose":
            source_pose_state: dict[str, list[float]] = {}
            source_pose_action_display_name = "Rest Pose"
        else:
            source_pose_action = source_selected_action if not source_pose_action_name else resolve_action(source_armature, source_pose_action_name)
            source_pose_state = capture_action_frame_rotation_state(
                source_armature,
                source_pose_action,
                int(source_pose_spec["frame"]),
            )
            source_pose_action_display_name = source_pose_action.name
        apply_source_pose_to_live_armature = bool(config.get("applySourcePoseToSourceArmature", False))

        scene = bpy.context.scene
        previous_scene_frame = scene.frame_current
        scene.frame_set(source_action_frame)
        assign_action_to_armature(source_armature, source_selected_action)
        bpy.context.view_layer.update()
        source_missing_bones: list[str] = []
        applied_source_pose_bones: list[str] = []
        if apply_source_pose_to_live_armature:
            source_missing_bones = apply_pose_rotation_state(
                source_armature,
                source_pose_state,
            )
            if not source_missing_bones:
                applied_source_pose_bones = sorted(source_pose_state.keys())

        target_rotation_state: dict[str, list[float]] = {}
        missing_target_pose_bones: list[str] = []
        applied_target_pose_bones: list[str] = []
        for bone_name, rotation_values in (target_pose_preset.get("bones") or {}).items():
            pose_bone = target_armature.pose.bones.get(str(bone_name))
            if pose_bone is None:
                missing_target_pose_bones.append(str(bone_name))
                continue
            target_rotation_state[str(bone_name)] = quaternion_to_list(
                rotation_values_to_quaternion(
                    [float(value) for value in rotation_values],
                    target_rotation_format,
                )
            )
            applied_target_pose_bones.append(str(bone_name))

        target_missing_apply_bones = []
        if not missing_target_pose_bones:
            target_missing_apply_bones = apply_pose_rotation_state(
                target_armature,
                target_rotation_state,
            )

        failures: list[str] = []
        if source_missing_bones:
            failures.append(
                f"Could not apply the source retarget pose to bones: {sorted(source_missing_bones)}"
            )
        if missing_target_pose_bones:
            failures.append(
                f"Target pose preset '{target_pose_preset_id}' is missing bones on the target armature: {sorted(missing_target_pose_bones)}"
            )
        if target_missing_apply_bones:
            failures.append(
                f"Could not apply the target retarget pose to bones: {sorted(target_missing_apply_bones)}"
            )

        pose_restore_runtime = {
            "sourceOriginalBasis": source_original_basis,
            "targetOriginalBasis": target_original_basis,
            "sourceActionName": source_selected_action.name,
            "sourceFrame": source_action_frame,
            "sceneFrameBeforeNormalization": previous_scene_frame,
        }
        pose_normalization = {
            "sourcePoseMode": source_pose_spec["mode"],
            "sourcePoseAction": source_pose_action_display_name,
            "sourcePoseFrame": int(source_pose_spec["frame"]),
            "appliedSourcePoseToSourceArmature": apply_source_pose_to_live_armature,
            "appliedSourcePoseBones": applied_source_pose_bones,
            "targetPoseMode": "preset",
            "targetPosePresetId": target_pose_preset_id,
            "appliedTargetPoseBones": sorted(applied_target_pose_bones),
            "missingTargetPoseBones": sorted(missing_target_pose_bones),
            "poseRestoreStatus": "scheduled",
        }

        artifact_bundle["poseRestoreRuntime"] = pose_restore_runtime
        artifact_bundle["sourcePoseRotationStateRuntime"] = source_pose_state
        artifact_bundle["targetPoseRotationStateRuntime"] = target_rotation_state
        artifact_bundle["poseNormalization"] = pose_normalization
        artifact_bundle["_stage_log_text"] = ""

        notes = []
        snapshot = maybe_save_snapshot(context, "after_normalize_retarget_pose", notes)
        metrics = dict(pose_normalization)
        metrics["sourcePoseBoneCount"] = len(source_pose_state)
        metrics["failureCount"] = len(failures)
        if snapshot:
            metrics["snapshotPath"] = snapshot

        return StageResult(
            stage_id=self.stage_id,
            enabled=True,
            status="failed" if failures else "completed",
            started_at=started_at,
            finished_at=utc_now(),
            metrics=metrics,
            debug_notes=notes,
            error="\n".join(failures) if failures else None,
        )


class RetargetToCitizenStage(Stage):
    stage_id = "retarget_to_citizen"

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        started_at = utc_now()
        source_armature = artifact_bundle.get("sourceArmature")
        target_armature = artifact_bundle.get("targetArmature")
        source_metrics = artifact_bundle.get("sourceMetrics")
        selected_action_name = artifact_bundle.get("selectedActionName")
        bone_map = artifact_bundle.get("boneMap") or {}
        rest_alignment_runtime = artifact_bundle.get("restAlignmentRuntime") or {}
        if source_armature is None or target_armature is None or not source_metrics or not selected_action_name:
            raise PipelineError("Retarget prerequisites are missing.")

        source_action = resolve_action(source_armature, selected_action_name)
        assign_action_to_armature(source_armature, source_action)
        source_leaf_action_name = source_metrics["selectedAction"]["leafName"]

        retarget_backend = str(config.get("retargetBackend", "native_experimental") or "").strip()
        if not retarget_backend:
            retarget_backend = "native_experimental"

        artifact_bundle["retargetBackend"] = retarget_backend
        artifact_bundle["backendVersion"] = artifact_bundle.get("backendVersion")
        artifact_bundle["backendSettings"] = {}
        artifact_bundle["backendGeneratedBoneMap"] = []
        artifact_bundle["backendWarnings"] = []

        if retarget_backend == "rokoko_operator":
            backend_metrics, backend_warnings, log_text = run_rokoko_backend(context, artifact_bundle, config)
            target_action = resolve_action(target_armature, None)
            assign_action_to_armature(target_armature, target_action)

            artifact_bundle["retargetedActionName"] = target_action.name
            artifact_bundle["targetActionName"] = target_action.name
            artifact_bundle["exportArmature"] = target_armature
            artifact_bundle["exportSelectedActionName"] = target_action.name
            artifact_bundle["backendSettings"] = backend_metrics["backendSettings"]
            artifact_bundle["_stage_log_text"] = log_text

            return StageResult(
                stage_id=self.stage_id,
                enabled=True,
                status="completed",
                started_at=started_at,
                finished_at=utc_now(),
                warnings=backend_warnings,
                metrics=backend_metrics,
            )
        if retarget_backend != "native_experimental":
            raise PipelineError(f"Unsupported retarget backend '{retarget_backend}'.")

        animated_slots = dedupe_preserve_order(
            [str(slot) for slot in config.get("animatedChains", list(bone_map.keys()))]
        )
        translation_slots = {str(slot) for slot in config.get("translationSlots", [])}
        chain_definitions = list(config.get("chainDefinitions", []))
        if not animated_slots and not chain_definitions:
            raise PipelineError("Retarget config does not define animatedChains or chainDefinitions.")

        chain_driven_slots: set[str] = set()
        chain_metrics: list[dict[str, Any]] = []
        for chain_config in chain_definitions:
            upper_slot = str(chain_config.get("upperSlot", "")).strip()
            lower_slot = str(chain_config.get("lowerSlot", "")).strip()
            effector_slot = str(chain_config.get("effectorSlot", "")).strip()
            follow_fk_slots = dedupe_preserve_order(
                [str(slot) for slot in chain_config.get("followFkSlots", [])]
            )
            for slot in [upper_slot, lower_slot, effector_slot, *follow_fk_slots]:
                if slot:
                    chain_driven_slots.add(slot)
            chain_metrics.append(
                {
                    "id": str(chain_config.get("id", f"chain_{len(chain_metrics)}")),
                    "mode": str(chain_config.get("mode", "two_bone_ik")),
                    "anchorSlot": str(chain_config.get("anchorSlot", "")).strip(),
                    "upperSlot": upper_slot,
                    "lowerSlot": lower_slot,
                    "effectorSlot": effector_slot,
                    "followFkSlots": follow_fk_slots,
                }
            )

        fk_copy_slots = dedupe_preserve_order(
            [str(slot) for slot in config.get("fkCopySlots", [])]
        )
        if not fk_copy_slots:
            fk_copy_slots = [slot for slot in animated_slots if slot not in chain_driven_slots]

        target_action_name = normalize_action_name_for_backend(
            context.target_profile,
            str(source_leaf_action_name),
            "native_experimental",
        )
        target_action = bpy.data.actions.new(target_action_name)
        assign_action_to_armature(target_armature, target_action)
        for pose_bone in target_armature.pose.bones:
            pose_bone.rotation_mode = "QUATERNION"

        frame_start, frame_end = source_metrics["selectedAction"]["frameRange"]
        scene = bpy.context.scene
        scene.frame_start = int(frame_start)
        scene.frame_end = int(frame_end)

        failures: list[str] = []
        resolved_chain_metrics: list[dict[str, Any]] = []

        def resolve_slot_runtime(slot: str) -> tuple[Any | None, Any | None, dict[str, Any] | None, str | None]:
            map_entry = bone_map.get(slot)
            rest_payload = rest_alignment_runtime.get(slot)
            if not map_entry or not rest_payload:
                return None, None, None, f"Retarget slot '{slot}' is missing from the bone map or rest alignment."

            source_pose_bone = source_armature.pose.bones.get(map_entry["sourceBone"])
            target_pose_bone = target_armature.pose.bones.get(map_entry["targetBone"])
            if source_pose_bone is None or target_pose_bone is None:
                return (
                    None,
                    None,
                    None,
                    f"Retarget slot '{slot}' references missing bones {map_entry['sourceBone']} -> {map_entry['targetBone']}.",
                )
            return source_pose_bone, target_pose_bone, rest_payload, None

        for frame in range(int(frame_start), int(frame_end) + 1):
            scene.frame_set(frame)
            bpy.context.view_layer.update()

            for pose_bone in target_armature.pose.bones:
                pose_bone.matrix_basis = Matrix.Identity(4)

            for slot in fk_copy_slots:
                source_pose_bone, target_pose_bone, rest_payload, error = resolve_slot_runtime(slot)
                if error:
                    failures.append(error)
                    continue
                apply_converted_local_delta(
                    source_pose_bone,
                    target_pose_bone,
                    rest_payload,
                    allow_translation=slot in translation_slots,
                )
                keyframe_pose_bone(target_pose_bone, frame)

            bpy.context.view_layer.update()

            for chain_config in chain_definitions:
                chain_id = str(chain_config.get("id", "unnamed_chain"))
                chain_mode = str(chain_config.get("mode", "two_bone_ik"))
                anchor_slot = str(chain_config.get("anchorSlot", "")).strip()
                upper_slot = str(chain_config.get("upperSlot", "")).strip()
                lower_slot = str(chain_config.get("lowerSlot", "")).strip()
                effector_slot = str(chain_config.get("effectorSlot", "")).strip()
                follow_fk_slots = dedupe_preserve_order(
                    [str(slot) for slot in chain_config.get("followFkSlots", [])]
                )
                chain_slots = [anchor_slot, upper_slot, lower_slot, effector_slot]
                if not all(chain_slots):
                    failures.append(
                        f"Chain '{chain_id}' is missing one of anchorSlot/upperSlot/lowerSlot/effectorSlot."
                    )
                    continue
                if chain_mode != "two_bone_ik":
                    failures.append(f"Chain '{chain_id}' uses unsupported mode '{chain_mode}'.")
                    continue

                source_anchor, target_anchor, _, error = resolve_slot_runtime(anchor_slot)
                if error:
                    failures.append(f"Chain '{chain_id}': {error}")
                    continue
                source_upper, target_upper, _, error = resolve_slot_runtime(upper_slot)
                if error:
                    failures.append(f"Chain '{chain_id}': {error}")
                    continue
                source_lower, target_lower, _, error = resolve_slot_runtime(lower_slot)
                if error:
                    failures.append(f"Chain '{chain_id}': {error}")
                    continue
                source_effector, target_effector, effector_rest_payload, error = resolve_slot_runtime(
                    effector_slot
                )
                if error:
                    failures.append(f"Chain '{chain_id}': {error}")
                    continue

                source_chain_length = max(
                    bone_rest_length(source_upper) + bone_rest_length(source_lower),
                    1e-6,
                )
                target_chain_length = max(
                    bone_rest_length(target_upper) + bone_rest_length(target_lower),
                    1e-6,
                )
                chain_scale = target_chain_length / source_chain_length

                source_anchor_matrix_inv = source_anchor.matrix.inverted()
                desired_effector_local = scaled_vector(
                    source_anchor_matrix_inv @ source_effector.head,
                    chain_scale,
                )
                desired_pole_local = scaled_vector(
                    source_anchor_matrix_inv @ source_lower.head,
                    chain_scale,
                )
                desired_effector_world = target_anchor.matrix @ desired_effector_local
                desired_pole_world = target_anchor.matrix @ desired_pole_local

                upper_start = target_upper.head.copy()
                solved_joint, solved_effector = solve_two_bone_joint(
                    upper_start,
                    desired_effector_world,
                    desired_pole_world,
                    bone_rest_length(target_upper),
                    bone_rest_length(target_lower),
                )

                align_bone_to_world_direction(target_upper, solved_joint - upper_start)
                bpy.context.view_layer.update()
                lower_start = target_lower.head.copy()
                align_bone_to_world_direction(target_lower, solved_effector - lower_start)
                bpy.context.view_layer.update()

                keyframe_pose_bone(target_upper, frame)
                keyframe_pose_bone(target_lower, frame)

                apply_converted_local_delta(
                    source_effector,
                    target_effector,
                    effector_rest_payload,
                    allow_translation=effector_slot in translation_slots,
                )
                keyframe_pose_bone(target_effector, frame)

                for follow_slot in follow_fk_slots:
                    source_follow, target_follow, follow_rest_payload, error = resolve_slot_runtime(follow_slot)
                    if error:
                        failures.append(f"Chain '{chain_id}': {error}")
                        continue
                    apply_converted_local_delta(
                        source_follow,
                        target_follow,
                        follow_rest_payload,
                        allow_translation=follow_slot in translation_slots,
                    )
                    keyframe_pose_bone(target_follow, frame)

                if frame == int(frame_start):
                    resolved_chain_metrics.append(
                        {
                            "id": chain_id,
                            "mode": chain_mode,
                            "anchorSlot": anchor_slot,
                            "upperSlot": upper_slot,
                            "lowerSlot": lower_slot,
                            "effectorSlot": effector_slot,
                            "followFkSlots": follow_fk_slots,
                            "sourceChainLength": round_float(source_chain_length),
                            "targetChainLength": round_float(target_chain_length),
                            "lengthScale": round_float(chain_scale),
                            "desiredEffectorAtStart": to_float_list(desired_effector_world),
                            "solvedEffectorAtStart": to_float_list(solved_effector),
                        }
                    )

        bpy.context.view_layer.update()
        artifact_bundle["retargetedActionName"] = target_action_name
        artifact_bundle["targetActionName"] = target_action_name
        artifact_bundle["exportArmature"] = target_armature
        artifact_bundle["exportSelectedActionName"] = target_action_name
        artifact_bundle["backendSettings"] = {
            "retargetBackend": "native_experimental",
            "translationSlots": sorted(translation_slots),
            "animatedSlots": animated_slots,
        }
        artifact_bundle["backendGeneratedBoneMap"] = [
            {
                "sourceBone": mapping["sourceBone"],
                "targetBone": mapping["targetBone"],
                "boneKey": slot,
                "canonicalSlot": slot,
                "isCustom": False,
            }
            for slot, mapping in bone_map.items()
        ]
        artifact_bundle["_stage_log_text"] = ""

        return StageResult(
            stage_id=self.stage_id,
            enabled=True,
            status="failed" if failures else "completed",
            started_at=started_at,
            finished_at=utc_now(),
            metrics={
                "retargetBackend": "native_experimental",
                "backendVersion": None,
                "backendSettings": artifact_bundle["backendSettings"],
                "backendGeneratedBoneMap": artifact_bundle["backendGeneratedBoneMap"],
                "retargetedActionName": target_action_name,
                "animatedSlotCount": len(animated_slots),
                "animatedSlots": animated_slots,
                "fkCopySlots": fk_copy_slots,
                "chainDefinitions": chain_metrics,
                "resolvedChains": resolved_chain_metrics,
                "translationSlots": sorted(translation_slots),
                "frameRange": [int(frame_start), int(frame_end)],
                "failureCount": len(failures),
            },
            error="\n".join(dedupe_preserve_order(failures)) if failures else None,
        )


class BakeTargetAnimationStage(Stage):
    stage_id = "bake_target_animation"

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        del config
        started_at = utc_now()
        target_armature = artifact_bundle.get("targetArmature")
        target_action_name = artifact_bundle.get("targetActionName")
        if target_armature is None or not target_action_name:
            raise PipelineError("Target armature or target action is missing before bake.")

        target_action = resolve_action(target_armature, target_action_name)
        assign_action_to_armature(target_armature, target_action)

        notes = []
        snapshot = maybe_save_snapshot(context, "after_bake_target_animation", notes)
        metrics = {
            "targetActionName": target_action.name,
            "frameRange": [int(value) for value in target_action.frame_range],
        }
        if snapshot:
            metrics["snapshotPath"] = snapshot

        artifact_bundle["_stage_log_text"] = ""
        return StageResult(
            stage_id=self.stage_id,
            enabled=True,
            status="completed",
            started_at=started_at,
            finished_at=utc_now(),
            metrics=metrics,
            debug_notes=notes,
        )


class RebuildCitizenIkStage(Stage):
    stage_id = "rebuild_citizen_ik"

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        started_at = utc_now()
        target_armature = artifact_bundle.get("targetArmature")
        target_action_name = artifact_bundle.get("targetActionName")
        if target_armature is None or not target_action_name:
            raise PipelineError("Target armature or target action is missing before IK rebuild.")

        target_action = resolve_action(target_armature, target_action_name)
        assign_action_to_armature(target_armature, target_action)

        frame_start, frame_end = [int(value) for value in target_action.frame_range]
        scene = bpy.context.scene
        scene.frame_start = frame_start
        scene.frame_end = frame_end

        enabled_targets = [str(name) for name in config.get("enabledTargets", [])]
        ik_target_pairs = context.target_profile.get("ikTargetPairs", {})
        failures: list[str] = []
        rebuilt_targets: list[str] = []

        for frame in range(frame_start, frame_end + 1):
            scene.frame_set(frame)
            bpy.context.view_layer.update()

            for ik_target_name in enabled_targets:
                ik_end_effector = ik_target_pairs.get(ik_target_name)
                ik_pose_bone = target_armature.pose.bones.get(ik_target_name)
                effector_pose_bone = target_armature.pose.bones.get(str(ik_end_effector))
                if ik_pose_bone is None or effector_pose_bone is None:
                    failures.append(
                        f"Could not rebuild IK target '{ik_target_name}' because its effector '{ik_end_effector}' is missing."
                    )
                    continue

                ik_pose_bone.rotation_mode = "QUATERNION"
                ik_pose_bone.matrix = effector_pose_bone.matrix.copy()
                ik_pose_bone.keyframe_insert(data_path="location", frame=frame, group=ik_pose_bone.name)
                ik_pose_bone.keyframe_insert(
                    data_path="rotation_quaternion",
                    frame=frame,
                    group=ik_pose_bone.name,
                )
                if ik_target_name not in rebuilt_targets:
                    rebuilt_targets.append(ik_target_name)

        bpy.context.view_layer.update()
        target_metrics = collect_armature_metrics(
            target_armature,
            context.target_profile,
            selected_action_name=target_action.name,
            allow_missing_action=False,
        )

        ik_summary = {
            "enabledTargets": enabled_targets,
            "rebuiltTargets": rebuilt_targets,
            "targetPairs": {
                target_name: ik_target_pairs.get(target_name)
                for target_name in enabled_targets
            },
        }

        artifact_bundle["ikRebuildSummary"] = ik_summary
        artifact_bundle["targetAnimationMetrics"] = target_metrics
        artifact_bundle["targetAnimationSummary"] = summarize_metrics(target_metrics)
        artifact_bundle["exportValidationMetrics"] = target_metrics
        artifact_bundle["exportValidationProfile"] = context.target_profile
        artifact_bundle["exportSelectedActionName"] = target_action.name
        artifact_bundle["_stage_log_text"] = ""

        return StageResult(
            stage_id=self.stage_id,
            enabled=True,
            status="failed" if failures else "completed",
            started_at=started_at,
            finished_at=utc_now(),
            metrics={
                "summary": ik_summary,
                "targetAnimationSummary": summarize_metrics(target_metrics),
                "failureCount": len(failures),
            },
            error="\n".join(dedupe_preserve_order(failures)) if failures else None,
        )


class AnalyzeMotionTrajectoriesStage(Stage):
    stage_id = "analyze_motion_trajectories"

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        started_at = utc_now()
        source_armature = artifact_bundle.get("sourceArmature")
        target_armature = artifact_bundle.get("targetArmature")
        selected_action_name = artifact_bundle.get("selectedActionName")
        target_action_name = artifact_bundle.get("targetActionName")
        bone_map = artifact_bundle.get("boneMap") or {}
        if source_armature is None or target_armature is None or not selected_action_name or not target_action_name:
            raise PipelineError("Motion-trajectory analysis prerequisites are missing.")

        tracked_slots = dedupe_preserve_order(
            [str(slot) for slot in config.get("trackedSlots", ["hand_L", "hand_R", "ankle_L", "ankle_R"])]
        )
        reference_slot = str(config.get("referenceSlot", "pelvis"))
        required_axis_slots = dedupe_preserve_order(
            [str(slot) for slot in config.get("requiredDominantAxisMatchSlots", tracked_slots)]
        )
        min_target_ankle_sign_changes = int(config.get("minTargetAnkleAlternationSignChanges", 2))
        fail_on_dominant_axis_mismatch = bool(config.get("failOnDominantAxisMismatch", True))
        fail_on_weak_ankle_alternation = bool(config.get("failOnWeakAnkleAlternation", True))

        failures: list[str] = []
        warnings: list[str] = []
        missing_slots = sorted(
            slot for slot in [reference_slot] + tracked_slots if slot not in bone_map
        )
        if missing_slots:
            failures.append(f"Motion analysis is missing mapped slots: {missing_slots}")

        if failures:
            return StageResult(
                stage_id=self.stage_id,
                enabled=True,
                status="failed",
                started_at=started_at,
                finished_at=utc_now(),
                error="\n".join(failures),
            )

        source_tracked_bones = {
            slot: str(bone_map[slot]["sourceBone"])
            for slot in tracked_slots
        }
        target_tracked_bones = {
            slot: str(bone_map[slot]["targetBone"])
            for slot in tracked_slots
        }
        source_reference_bone = str(bone_map[reference_slot]["sourceBone"])
        target_reference_bone = str(bone_map[reference_slot]["targetBone"])

        source_action = resolve_action(source_armature, selected_action_name)
        target_action = resolve_action(target_armature, target_action_name)

        source_trajectories, source_missing_bones = capture_relative_trajectories(
            source_armature,
            source_action,
            source_tracked_bones,
            source_reference_bone,
        )
        target_trajectories, target_missing_bones = capture_relative_trajectories(
            target_armature,
            target_action,
            target_tracked_bones,
            target_reference_bone,
        )

        if source_missing_bones:
            failures.append(
                f"Source motion analysis could not resolve bones: {sorted(source_missing_bones)}"
            )
        if target_missing_bones:
            failures.append(
                f"Target motion analysis could not resolve bones: {sorted(target_missing_bones)}"
            )

        slot_comparisons = {}
        mismatched_dominant_axis_slots: list[str] = []
        for slot in tracked_slots:
            if slot not in source_trajectories or slot not in target_trajectories:
                continue
            comparison = compare_slot_trajectories(
                source_trajectories[slot],
                target_trajectories[slot],
            )
            slot_comparisons[slot] = comparison
            if slot in required_axis_slots and not comparison.get("dominantAxisMatches"):
                mismatched_dominant_axis_slots.append(slot)

        if mismatched_dominant_axis_slots:
            message = (
                "Dominant swing axis did not match the source for slots: "
                f"{sorted(mismatched_dominant_axis_slots)}"
            )
            if fail_on_dominant_axis_mismatch:
                failures.append(message)
            else:
                warnings.append(message)

        ankle_alternation = build_ankle_alternation_summary(
            source_trajectories,
            target_trajectories,
        )
        target_sign_changes = ankle_alternation.get("targetSignChangesOnSourceAxis")
        if target_sign_changes is not None and target_sign_changes < min_target_ankle_sign_changes:
            message = (
                "Target ankle alternation on the source dominant axis was too weak: "
                f"{target_sign_changes} < {min_target_ankle_sign_changes}"
            )
            if fail_on_weak_ankle_alternation:
                failures.append(message)
            else:
                warnings.append(message)

        def compact(summary: dict[str, Any]) -> dict[str, Any]:
            return {
                key: value
                for key, value in summary.items()
                if key != "samples"
            }

        compact_source_trajectories = {
            slot: compact(summary)
            for slot, summary in source_trajectories.items()
        }
        compact_target_trajectories = {
            slot: compact(summary)
            for slot, summary in target_trajectories.items()
        }

        analysis_summary = {
            "referenceSlot": reference_slot,
            "trackedSlots": tracked_slots,
            "requiredDominantAxisMatchSlots": required_axis_slots,
            "source": compact_source_trajectories,
            "target": compact_target_trajectories,
            "comparisons": slot_comparisons,
            "ankleAlternation": ankle_alternation,
        }

        artifact_bundle["motionTrajectoryAnalysis"] = analysis_summary
        artifact_bundle["_stage_log_text"] = ""

        return StageResult(
            stage_id=self.stage_id,
            enabled=True,
            status="failed" if failures else "completed",
            started_at=started_at,
            finished_at=utc_now(),
            warnings=warnings,
            metrics=analysis_summary,
            error="\n".join(failures) if failures else None,
        )


class ValidateTargetAnimationStage(Stage):
    stage_id = "validate_target_animation"

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        started_at = utc_now()
        target_metrics = artifact_bundle.get("targetAnimationMetrics")
        bone_map = artifact_bundle.get("boneMap") or {}
        ik_summary = artifact_bundle.get("ikRebuildSummary") or {}
        if not target_metrics:
            raise PipelineError("Target animation metrics are missing.")

        animated_bones = set((target_metrics.get("selectedAction") or {}).get("animatedBoneNames", []))
        required_target_bones = sorted(entry["targetBone"] for entry in bone_map.values())
        missing_required_animated = sorted(
            bone_name for bone_name in required_target_bones if bone_name not in animated_bones
        )

        static_group_names = [str(name) for name in config.get("staticBoneGroups", [])]
        target_groups = target_metrics.get("boneGroups") or resolve_bone_groups(
            context.target_profile,
            target_metrics["boneNames"],
        )
        static_target_bones = sorted(
            {
                bone_name
                for group_name in static_group_names
                for bone_name in target_groups.get(group_name, [])
            }
        )
        unexpected_animated_static = sorted(
            bone_name for bone_name in static_target_bones if bone_name in animated_bones
        )

        enabled_ik_targets = sorted(ik_summary.get("enabledTargets", []))
        missing_ik_targets = sorted(
            target_name for target_name in enabled_ik_targets if target_name not in animated_bones
        )
        remaining_ik_bones = sorted(
            bone_name
            for bone_name in target_groups.get("ik", [])
            if bone_name not in enabled_ik_targets and bone_name in animated_bones
        )

        failures: list[str] = []
        if missing_required_animated:
            failures.append(f"Required target deform bones were not animated: {missing_required_animated}")
        if missing_ik_targets:
            failures.append(f"IK target bones were not animated: {missing_ik_targets}")
        if unexpected_animated_static:
            failures.append(
                f"Static target bones received animation unexpectedly: {unexpected_animated_static}"
            )
        if remaining_ik_bones:
            failures.append(
                f"Non-enabled IK bones should remain static but were animated: {remaining_ik_bones}"
            )

        artifact_bundle["staticTargetBones"] = static_target_bones
        artifact_bundle["_stage_log_text"] = ""

        notes = []
        snapshot = maybe_save_snapshot(context, "after_validate_target_animation", notes)
        metrics = {
            "summary": summarize_metrics(target_metrics),
            "requiredTargetBones": required_target_bones,
            "missingRequiredAnimatedBones": missing_required_animated,
            "enabledIkTargets": enabled_ik_targets,
            "missingAnimatedIkTargets": missing_ik_targets,
            "staticTargetBones": static_target_bones,
            "unexpectedAnimatedStaticBones": unexpected_animated_static,
            "unexpectedAnimatedOtherIkBones": remaining_ik_bones,
        }
        if snapshot:
            metrics["snapshotPath"] = snapshot

        fail_on_issues = bool(config.get("failOnIssues", False))
        warnings = [f"Target animation validation: {failure}" for failure in failures]

        return StageResult(
            stage_id=self.stage_id,
            enabled=True,
            status="failed" if fail_on_issues and failures else "completed",
            started_at=started_at,
            finished_at=utc_now(),
            warnings=warnings,
            metrics=metrics,
            debug_notes=notes,
            error="\n".join(failures) if fail_on_issues and failures else None,
        )


class ExportFbxStage(Stage):
    stage_id = "export_fbx"

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        started_at = utc_now()
        validation_metrics = artifact_bundle.get("exportValidationMetrics") or artifact_bundle.get("sourceMetrics")
        selected_action_name = artifact_bundle.get("exportSelectedActionName") or artifact_bundle.get("selectedActionName")
        armature = artifact_bundle.get("exportArmature") or require_single_armature()
        if not validation_metrics or not selected_action_name:
            raise PipelineError("Export validation metrics or selected action name are missing.")

        selected_action = resolve_action(armature, selected_action_name)
        assign_action_to_armature(armature, selected_action)
        export_bone_names = resolve_animation_export_bone_names(context.target_profile, armature)
        export_armature = armature
        export_validation_metrics = validation_metrics

        if sorted(export_bone_names) != sorted(bone.name for bone in armature.data.bones):
            export_armature = create_export_armature_copy(armature, selected_action, export_bone_names)
            export_validation_metrics = collect_armature_metrics(
                export_armature,
                context.target_profile,
                selected_action.name,
            )

        try:
            bpy.ops.object.mode_set(mode="OBJECT")
        except Exception:
            pass
        bpy.ops.object.select_all(action="DESELECT")
        export_armature.hide_set(False)
        export_armature.select_set(True)
        bpy.context.view_layer.objects.active = export_armature

        frame_start, frame_end = export_validation_metrics["selectedAction"]["frameRange"]
        bpy.context.scene.frame_start = int(frame_start)
        bpy.context.scene.frame_end = int(frame_end)
        bpy.context.scene.frame_set(int(frame_start))

        name_stem = context.input_path.stem
        if artifact_bundle.get("targetReferencePath"):
            name_stem = Path(str(artifact_bundle["targetReferencePath"])).stem
        output_name = (
            f"{sanitize_name(name_stem)}"
            f"__{sanitize_name(action_leaf_name(selected_action.name))}.fbx"
        )
        export_path = context.exports_dir / output_name

        def exporter() -> None:
            bpy.ops.export_scene.fbx(
                filepath=str(export_path),
                check_existing=False,
                path_mode="AUTO",
                use_selection=True,
                global_scale=float(config.get("globalScale", 1.0)),
                axis_forward=str(config.get("axisForward", "-Z")),
                axis_up=str(config.get("axisUp", "Y")),
                primary_bone_axis=str(config.get("primaryBoneAxis", "Y")),
                secondary_bone_axis=str(config.get("secondaryBoneAxis", "X")),
                apply_unit_scale=bool(config.get("applyUnitScale", True)),
                object_types={"ARMATURE"},
                use_armature_deform_only=bool(config.get("useArmatureDeformOnly", False)),
                add_leaf_bones=bool(config.get("addLeafBones", False)),
                bake_anim=bool(config.get("bakeAnim", True)),
                bake_anim_use_all_bones=bool(config.get("bakeAnimUseAllBones", True)),
                bake_anim_use_all_actions=bool(config.get("bakeAnimUseAllActions", False)),
                bake_anim_use_nla_strips=bool(config.get("bakeAnimUseNlaStrips", False)),
                bake_anim_simplify_factor=float(config.get("bakeAnimSimplifyFactor", 0.0)),
            )

        export_armature_name = export_armature.name
        try:
            log_text, warnings = capture_operator_output(exporter)
        finally:
            if export_armature is not armature:
                cleanup_armature_object(export_armature)

        artifact_bundle["exportPath"] = str(export_path)
        artifact_bundle["exportWarnings"] = warnings
        artifact_bundle["exportValidationMetrics"] = export_validation_metrics
        artifact_bundle["exportSelectedActionName"] = selected_action.name
        artifact_bundle["_stage_log_text"] = log_text

        notes = []
        snapshot = maybe_save_snapshot(context, "before_validate_export", notes)

        metrics = {
            "exportPath": str(export_path),
            "exportExists": export_path.exists(),
            "exportSizeBytes": export_path.stat().st_size if export_path.exists() else None,
            "selectedAction": action_leaf_name(selected_action.name),
            "exportArmatureName": export_armature_name,
            "exportBoneCount": len(export_bone_names),
        }
        if snapshot:
            metrics["snapshotPath"] = snapshot

        return StageResult(
            stage_id=self.stage_id,
            enabled=True,
            status="completed" if export_path.exists() else "failed",
            started_at=started_at,
            finished_at=utc_now(),
            warnings=warnings,
            metrics=metrics,
            debug_notes=notes,
            error=None if export_path.exists() else "FBX export file was not created.",
        )


class ValidateExportStage(Stage):
    stage_id = "validate_export"

    def run(
        self,
        context: RunContext,
        artifact_bundle: dict[str, Any],
        config: dict[str, Any],
    ) -> StageResult:
        started_at = utc_now()
        source_metrics = artifact_bundle.get("exportValidationMetrics") or artifact_bundle.get("sourceMetrics")
        export_path = artifact_bundle.get("exportPath")
        validation_profile = artifact_bundle.get("exportValidationProfile") or context.source_profile
        selected_action_name = artifact_bundle.get("exportSelectedActionName") or artifact_bundle.get("selectedActionName")
        if not source_metrics or not export_path or not selected_action_name:
            raise PipelineError("Export validation inputs are missing.")

        export_metrics, log_text, warnings = inspect_fbx_file(
            Path(export_path),
            validation_profile,
            selected_action_name,
            build_import_settings(artifact_bundle["importConfig"]),
        )
        artifact_bundle["exportMetrics"] = export_metrics
        artifact_bundle["_stage_log_text"] = log_text

        failures, comparison_warnings, comparison = compare_metrics(
            source_metrics,
            export_metrics,
            config,
        )

        notes = []
        snapshot = maybe_save_snapshot(context, "after_validate_export_import", notes)
        metrics = {
            "sourceSummary": summarize_metrics(source_metrics),
            "exportSummary": summarize_metrics(export_metrics),
            "comparison": comparison,
        }
        if snapshot:
            metrics["snapshotPath"] = snapshot

        all_warnings = list(artifact_bundle.get("exportWarnings", [])) + warnings + comparison_warnings
        return StageResult(
            stage_id=self.stage_id,
            enabled=True,
            status="failed" if failures else "completed",
            started_at=started_at,
            finished_at=utc_now(),
            warnings=all_warnings,
            metrics=metrics,
            debug_notes=notes,
            error="\n".join(failures) if failures else None,
        )


STAGE_REGISTRY: dict[str, Stage] = {
    "import_fbx": ImportFbxStage(),
    "collect_source_metadata": CollectSourceMetadataStage(),
    "validate_import": ValidateImportStage(),
    "load_target_reference": LoadTargetReferenceStage(),
    "collect_target_metadata": CollectTargetMetadataStage(),
    "align_rest_pose": AlignRestPoseStage(),
    "build_bone_map": BuildBoneMapStage(),
    "normalize_retarget_pose": NormalizeRetargetPoseStage(),
    "retarget_to_citizen": RetargetToCitizenStage(),
    "bake_target_animation": BakeTargetAnimationStage(),
    "rebuild_citizen_ik": RebuildCitizenIkStage(),
    "analyze_motion_trajectories": AnalyzeMotionTrajectoriesStage(),
    "validate_target_animation": ValidateTargetAnimationStage(),
    "export_fbx": ExportFbxStage(),
    "validate_export": ValidateExportStage(),
}


def build_context(args: argparse.Namespace) -> RunContext:
    root = script_root()
    recipe_path = Path(args.recipe).resolve()
    if not recipe_path.is_file():
        raise PipelineError(f"Recipe file not found: {recipe_path}")

    recipe = load_json_file(recipe_path)
    source_profile_path = resolve_named_json(
        root / "config" / "source_profiles",
        args.source_profile or recipe.get("sourceProfile"),
    )
    target_profile_path = resolve_named_json(
        root / "config" / "target_profiles",
        args.target_profile or recipe.get("targetProfile"),
    )
    source_profile = load_json_file(source_profile_path)
    target_profile = load_json_file(target_profile_path)

    output_root = Path(args.output_root).resolve()
    bone_map_overrides_path = (
        Path(args.bone_map_overrides).resolve() if args.bone_map_overrides else None
    )
    raw_bone_map_overrides = (
        load_json_file(bone_map_overrides_path)
        if bone_map_overrides_path is not None and bone_map_overrides_path.is_file()
        else {}
    )
    if bone_map_overrides_path is not None and not bone_map_overrides_path.is_file():
        raise PipelineError(f"Bone-map override file not found: {bone_map_overrides_path}")

    run_id = args.run_id or f"{datetime.now().strftime('%Y%m%d_%H%M%S')}_{uuid.uuid4().hex[:8]}"
    run_dir = ensure_dir(output_root / run_id)

    context = RunContext(
        recipe_path=recipe_path,
        recipe=recipe,
        source_profile_path=source_profile_path,
        source_profile=source_profile,
        target_profile_path=target_profile_path,
        target_profile=target_profile,
        input_path=Path(args.input).resolve(),
        output_root=output_root,
        selected_action_name=args.action,
        target_pose_preset_override=args.target_pose_preset,
        bone_map_overrides_path=bone_map_overrides_path,
        bone_map_overrides=normalize_external_bone_map_overrides(
            target_profile,
            raw_bone_map_overrides,
        ),
        run_id=run_id,
        run_dir=run_dir,
        debug_enabled=bool(recipe.get("debug", {}).get("enabled", False)),
        save_intermediate_blend_files=bool(recipe.get("debug", {}).get("saveIntermediateBlendFiles", False)),
        save_stage_metrics=bool(recipe.get("debug", {}).get("saveStageMetrics", False)),
    )

    ensure_dir(context.logs_dir)
    ensure_dir(context.metrics_dir)
    ensure_dir(context.exports_dir)
    if context.debug_enabled and context.save_intermediate_blend_files:
        ensure_dir(context.snapshots_dir)

    return context


def pipeline_status(stage_results: list[StageResult]) -> str:
    for stage_result in stage_results:
        if stage_result.status == "failed":
            return "failed"
    return "completed"


def write_manifest(context: RunContext, artifact_bundle: dict[str, Any]) -> Path:
    manifest_path = context.run_dir / "result_manifest.json"
    source_summary = summarize_metrics(artifact_bundle.get("sourceMetrics"))
    export_summary = summarize_metrics(artifact_bundle.get("exportMetrics"))
    target_summary = summarize_metrics(artifact_bundle.get("targetMetrics"))
    target_animation_summary = artifact_bundle.get("targetAnimationSummary") or summarize_metrics(
        artifact_bundle.get("targetAnimationMetrics")
    )
    payload = {
        "runId": context.run_id,
        "status": pipeline_status(context.stage_results),
        "recipeId": context.recipe.get("recipeId"),
        "recipePath": str(context.recipe_path),
        "sourceProfileId": context.source_profile.get("profileId"),
        "sourceProfilePath": str(context.source_profile_path),
        "targetProfileId": context.target_profile.get("profileId"),
        "targetProfilePath": str(context.target_profile_path),
        "sourceFile": str(context.input_path),
        "targetReference": artifact_bundle.get("targetReferencePath"),
        "selectedAction": artifact_bundle.get("selectedActionName"),
        "retargetedActionName": artifact_bundle.get("retargetedActionName"),
        "retargetBackend": artifact_bundle.get("retargetBackend"),
        "backendVersion": artifact_bundle.get("backendVersion"),
        "backendSettings": artifact_bundle.get("backendSettings"),
        "backendCustomSchemeSave": artifact_bundle.get("backendCustomSchemeSave"),
        "backendGeneratedBoneMap": artifact_bundle.get("backendGeneratedBoneMap"),
        "backendMappingOverrideSummary": artifact_bundle.get("backendMappingOverrideSummary"),
        "backendWarnings": artifact_bundle.get("backendWarnings", []),
        "poseNormalization": artifact_bundle.get("poseNormalization"),
        "sourcePoseReference": artifact_bundle.get("backendSourcePoseReference"),
        "motionTrajectoryAnalysis": artifact_bundle.get("motionTrajectoryAnalysis"),
        "importConfig": artifact_bundle.get("importConfig"),
        "boneMap": artifact_bundle.get("boneMap"),
        "mappingCoverage": artifact_bundle.get("mappingCoverage"),
        "boneMapOverrideSummary": artifact_bundle.get("boneMapOverrideSummary"),
        "unmappedRequiredSlots": artifact_bundle.get("unmappedRequiredSlots", []),
        "staticTargetBones": artifact_bundle.get("staticTargetBones"),
        "ikRebuildSummary": artifact_bundle.get("ikRebuildSummary"),
        "warnings": context.global_warnings,
        "stages": [stage_result.to_dict() for stage_result in context.stage_results],
        "metrics": {
            "sourceSummary": source_summary,
            "targetSummary": target_summary,
            "targetAnimationSummary": target_animation_summary,
            "exportSummary": export_summary,
            "poseNormalization": artifact_bundle.get("poseNormalization"),
            "sourcePoseReference": artifact_bundle.get("backendSourcePoseReference"),
            "motionTrajectoryAnalysis": artifact_bundle.get("motionTrajectoryAnalysis"),
            "customSchemeSave": artifact_bundle.get("backendCustomSchemeSave"),
            "boneMapOverrideSummary": artifact_bundle.get("boneMapOverrideSummary"),
            "targetFingerChainDetectionOverride": artifact_bundle.get("backendTargetFingerChainDetectionOverride"),
            "targetFingerChainRemap": artifact_bundle.get("backendTargetFingerChainRemap"),
            "normalizedFrameRanges": {
                "source": source_summary["selectedAction"]["frameRange"] if source_summary and source_summary.get("selectedAction") else None,
                "target": target_animation_summary["selectedAction"]["frameRange"] if target_animation_summary and target_animation_summary.get("selectedAction") else None,
                "export": export_summary["selectedAction"]["frameRange"] if export_summary and export_summary.get("selectedAction") else None,
            },
        },
        "outputs": {
            "runDir": str(context.run_dir),
            "exportPath": artifact_bundle.get("exportPath"),
            "manifestPath": str(manifest_path),
            "boneMapOverridesPath": str(context.bone_map_overrides_path) if context.bone_map_overrides_path else None,
        },
    }
    write_json(manifest_path, payload)
    return manifest_path


def run_stage(
    context: RunContext,
    artifact_bundle: dict[str, Any],
    stage_id: str,
    stage_config: dict[str, Any],
) -> StageResult:
    if not bool(stage_config.get("enabled", True)):
        return mark_skipped(stage_id, False, "Stage disabled by recipe.")

    stage = STAGE_REGISTRY.get(stage_id)
    if stage is None:
        raise PipelineError(f"Unsupported stage '{stage_id}'.")

    try:
        stage_result = stage.run(context, artifact_bundle, stage_config.get("config", {}))
    except Exception as exc:
        artifact_bundle["_stage_log_text"] = traceback.format_exc()
        stage_result = StageResult(
            stage_id=stage_id,
            enabled=True,
            status="failed",
            started_at=utc_now(),
            finished_at=utc_now(),
            error=f"{type(exc).__name__}: {exc}",
            debug_notes=["Stage raised an exception."],
        )

    persist_stage_artifacts(context, stage_result, artifact_bundle)
    artifact_bundle.pop("_stage_log_text", None)
    return stage_result


def main() -> int:
    args = parse_args()
    context = build_context(args)
    artifact_bundle: dict[str, Any] = {}
    stage_configs = context.recipe.get("stages", [])

    abort_remaining = False
    manifest_stage_enabled = False

    for stage_config in stage_configs:
        stage_id = stage_config.get("id")
        if stage_id == "write_result_manifest":
            manifest_stage_enabled = bool(stage_config.get("enabled", True))
            continue

        if abort_remaining:
            context.stage_results.append(
                mark_skipped(stage_id, bool(stage_config.get("enabled", True)), "Skipped because an earlier stage failed.")
            )
            continue

        stage_result = run_stage(context, artifact_bundle, stage_id, stage_config)
        context.stage_results.append(stage_result)
        context.global_warnings.extend(stage_result.warnings)
        if stage_result.status == "failed":
            abort_remaining = True

    manifest_path = write_manifest(context, artifact_bundle)
    manifest_result = StageResult(
        stage_id="write_result_manifest",
        enabled=manifest_stage_enabled,
        status="completed" if manifest_stage_enabled else "skipped",
        started_at=utc_now(),
        finished_at=utc_now(),
        metrics={"manifestPath": str(manifest_path)},
        outputs={"manifestPath": str(manifest_path)},
    )
    context.stage_results.append(manifest_result)
    write_manifest(context, artifact_bundle)

    final_status = pipeline_status(context.stage_results)
    print(f"RUN_ID {context.run_id}")
    print(f"RUN_STATUS {final_status}")
    print(f"MANIFEST {manifest_path}")
    return 0 if final_status == "completed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
