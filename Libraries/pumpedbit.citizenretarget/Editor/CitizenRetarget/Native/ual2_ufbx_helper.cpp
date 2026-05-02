#include "ufbx/ufbx.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <iomanip>
#include <sstream>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

struct simple_vec3 {
	double x;
	double y;
	double z;
};

struct simple_quat {
	double x;
	double y;
	double z;
	double w;
};

struct simple_transform {
	simple_vec3 translation;
	simple_quat rotation;
};

struct included_node {
	const ufbx_node *node;
	const ufbx_node *included_parent;
	std::string name;
	std::string parent_name;
};

enum class load_profile {
	current,
	modify_geometry,
	raw_units,
	target_only_control,
};

static std::string to_string(ufbx_string value)
{
	return value.data ? std::string(value.data, value.length) : std::string();
}

static std::string json_escape(std::string_view value)
{
	std::string escaped;
	escaped.reserve(value.size() + 8);
	for (char c : value) {
		switch (c) {
		case '\\': escaped += "\\\\"; break;
		case '"': escaped += "\\\""; break;
		case '\n': escaped += "\\n"; break;
		case '\r': escaped += "\\r"; break;
		case '\t': escaped += "\\t"; break;
		default: escaped += c; break;
		}
	}
	return escaped;
}

static bool iequals(std::string_view a, std::string_view b)
{
	return a.size() == b.size()
		&& std::equal(a.begin(), a.end(), b.begin(), [](char lhs, char rhs) {
			return std::tolower(static_cast<unsigned char>(lhs)) == std::tolower(static_cast<unsigned char>(rhs));
		});
}

static std::string normalize_clip_name(const std::string &name)
{
	auto pos = name.find_last_of('|');
	if (pos != std::string::npos && pos + 1 < name.size()) {
		return name.substr(pos + 1);
	}

	pos = name.find_last_of(':');
	if (pos != std::string::npos && pos + 1 < name.size()) {
		return name.substr(pos + 1);
	}

	return name;
}

static simple_vec3 make_vec3(ufbx_vec3 value)
{
	return { value.x, value.y, value.z };
}

static simple_quat make_quat(ufbx_quat value)
{
	return { value.x, value.y, value.z, value.w };
}

static simple_quat quat_normalize(simple_quat value)
{
	const auto length = std::sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
	if (length <= 1e-12) {
		return { 0.0, 0.0, 0.0, 1.0 };
	}

	const auto inv = 1.0 / length;
	return { value.x * inv, value.y * inv, value.z * inv, value.w * inv };
}

static simple_transform make_transform(ufbx_transform value)
{
	return { make_vec3(value.translation), quat_normalize(make_quat(value.rotation)) };
}

static simple_transform transform_from_matrix(const ufbx_matrix &value)
{
	return make_transform(ufbx_matrix_to_transform(&value));
}

static bool try_parse_profile(std::string_view name, load_profile &profile)
{
	if (iequals(name, "Current")) {
		profile = load_profile::current;
		return true;
	}
	if (iequals(name, "ModifyGeometry")) {
		profile = load_profile::modify_geometry;
		return true;
	}
	if (iequals(name, "RawUnits")) {
		profile = load_profile::raw_units;
		return true;
	}
	if (iequals(name, "TargetOnlyControl")) {
		profile = load_profile::target_only_control;
		return true;
	}

	return false;
}

static const char *profile_name(load_profile profile)
{
	switch (profile) {
	case load_profile::current: return "Current";
	case load_profile::modify_geometry: return "ModifyGeometry";
	case load_profile::raw_units: return "RawUnits";
	case load_profile::target_only_control: return "TargetOnlyControl";
	default: return "Current";
	}
}

static ufbx_load_opts make_load_opts(load_profile profile)
{
	ufbx_load_opts opts = {};
	opts.target_axes = ufbx_axes_right_handed_z_up;

	switch (profile) {
	case load_profile::current:
		opts.target_unit_meters = 0.01f;
		opts.space_conversion = UFBX_SPACE_CONVERSION_ADJUST_TRANSFORMS;
		opts.inherit_mode_handling = UFBX_INHERIT_MODE_HANDLING_IGNORE;
		opts.geometry_transform_handling = UFBX_GEOMETRY_TRANSFORM_HANDLING_HELPER_NODES;
		break;
	case load_profile::modify_geometry:
		opts.target_unit_meters = 0.01f;
		opts.space_conversion = UFBX_SPACE_CONVERSION_MODIFY_GEOMETRY;
		opts.inherit_mode_handling = UFBX_INHERIT_MODE_HANDLING_IGNORE;
		opts.geometry_transform_handling = UFBX_GEOMETRY_TRANSFORM_HANDLING_HELPER_NODES;
		break;
	case load_profile::raw_units:
		opts.target_unit_meters = 0.0f;
		opts.space_conversion = UFBX_SPACE_CONVERSION_ADJUST_TRANSFORMS;
		opts.inherit_mode_handling = UFBX_INHERIT_MODE_HANDLING_PRESERVE;
		opts.geometry_transform_handling = UFBX_GEOMETRY_TRANSFORM_HANDLING_PRESERVE;
		break;
	case load_profile::target_only_control:
		opts.target_unit_meters = 0.0f;
		opts.space_conversion = UFBX_SPACE_CONVERSION_ADJUST_TRANSFORMS;
		opts.inherit_mode_handling = UFBX_INHERIT_MODE_HANDLING_IGNORE;
		opts.geometry_transform_handling = UFBX_GEOMETRY_TRANSFORM_HANDLING_HELPER_NODES;
		break;
	}

	return opts;
}

static ufbx_scene *load_scene(const char *path, load_profile profile, std::string &error_text)
{
	ufbx_error error = {};
	ufbx_load_opts opts = make_load_opts(profile);
	auto *scene = ufbx_load_file_len(path, std::strlen(path), &opts, &error);
	if (scene) {
		return scene;
	}

	error_text = to_string(error.description);
	if (error.info_length > 0) {
		error_text += ": ";
		error_text += error.info;
	}

	if (error_text.empty()) {
		error_text = "Unknown ufbx load error";
	}

	return nullptr;
}

static double output_unit_meters(const ufbx_scene *scene, load_profile profile)
{
	const auto opts = make_load_opts(profile);
	return opts.target_unit_meters > 0.0 ? opts.target_unit_meters : scene->settings.unit_meters;
}

static std::vector<included_node> collect_included_nodes(const ufbx_scene *scene)
{
	std::vector<included_node> result;
	result.reserve(scene->nodes.count);

	for (size_t index = 0; index < scene->nodes.count; ++index) {
		const ufbx_node *node = scene->nodes.data[index];
		if (!node->bone) {
			continue;
		}

		const ufbx_node *included_parent = nullptr;
		for (const ufbx_node *parent = node->parent; parent; parent = parent->parent) {
			if (parent->bone) {
				included_parent = parent;
				break;
			}
		}

		result.push_back({
			node,
			included_parent,
			to_string(node->name),
			included_parent ? to_string(included_parent->name) : std::string()
		});
	}

	return result;
}

static ufbx_matrix evaluate_world_matrix(
	const ufbx_anim *anim,
	const ufbx_node *node,
	double time,
	std::unordered_map<uint32_t, ufbx_matrix> &cache)
{
	auto found = cache.find(node->typed_id);
	if (found != cache.end()) {
		return found->second;
	}

	ufbx_matrix parent_world = ufbx_identity_matrix;
	bool has_parent = false;
	if (node->parent) {
		parent_world = evaluate_world_matrix(anim, node->parent, time, cache);
		has_parent = true;
	}

	const auto local = anim ? ufbx_evaluate_transform(anim, node, time) : node->local_transform;
	const auto local_matrix = ufbx_transform_to_matrix(&local);
	const auto world = has_parent ? ufbx_matrix_mul(&parent_world, &local_matrix) : local_matrix;
	cache.emplace(node->typed_id, world);
	return world;
}

static void append_transform_array(std::ostringstream &stream, const simple_transform &transform)
{
	stream << "[" << transform.translation.x << "," << transform.translation.y << "," << transform.translation.z << "]";
}

static void append_rotation_array(std::ostringstream &stream, const simple_transform &transform)
{
	stream << "[" << transform.rotation.x << "," << transform.rotation.y << "," << transform.rotation.z << "," << transform.rotation.w << "]";
}

static void append_scale_array(std::ostringstream &stream, ufbx_vec3 scale)
{
	stream << "[" << scale.x << "," << scale.y << "," << scale.z << "]";
}

static void append_metadata_json(std::ostringstream &stream, const ufbx_metadata &metadata)
{
	stream << "\"metadata\":{";
	stream << "\"creator\":\"" << json_escape(to_string(metadata.creator)) << "\",";
	stream << "\"originalApplication\":{";
	stream << "\"name\":\"" << json_escape(to_string(metadata.original_application.name)) << "\",";
	stream << "\"vendor\":\"" << json_escape(to_string(metadata.original_application.vendor)) << "\",";
	stream << "\"version\":\"" << json_escape(to_string(metadata.original_application.version)) << "\"";
	stream << "},";
	stream << "\"latestApplication\":{";
	stream << "\"name\":\"" << json_escape(to_string(metadata.latest_application.name)) << "\",";
	stream << "\"vendor\":\"" << json_escape(to_string(metadata.latest_application.vendor)) << "\",";
	stream << "\"version\":\"" << json_escape(to_string(metadata.latest_application.version)) << "\"";
	stream << "}";
	stream << "}";
}

static const ufbx_anim_stack *find_clip(const ufbx_scene *scene, std::string_view clip_name)
{
	for (size_t index = 0; index < scene->anim_stacks.count; ++index) {
		const auto *stack = scene->anim_stacks.data[index];
		const auto full_name = to_string(stack->name);
		const auto display_name = normalize_clip_name(full_name);
		if (full_name == clip_name || iequals(full_name, clip_name) || display_name == clip_name || iequals(display_name, clip_name)) {
			return stack;
		}
	}

	return nullptr;
}

static std::string make_error_json(const std::string &message)
{
	return std::string("{\"error\":\"") + json_escape(message) + "\"}";
}

static char *copy_result(const std::string &text)
{
	auto *buffer = static_cast<char*>(std::malloc(text.size() + 1));
	if (!buffer) {
		return nullptr;
	}

	std::memcpy(buffer, text.data(), text.size());
	buffer[text.size()] = '\0';
	return buffer;
}

extern "C" __declspec(dllexport) const char *ual2_sample_clip_json_ex(
	const char *source_path,
	const char *clip_name,
	const char *target_path,
	const char *source_profile_name,
	const char *target_profile_name);

extern "C" __declspec(dllexport) const char *ual2_scan_fbx_json(const char *source_path)
{
	if (!source_path || !source_path[0]) {
		return copy_result(make_error_json("Source FBX path is empty"));
	}

	std::string error_text;
	ufbx_scene *scene = load_scene(source_path, load_profile::current, error_text);
	if (!scene) {
		return copy_result(make_error_json(error_text));
	}

	std::ostringstream stream;
	stream << std::fixed << std::setprecision(6);
	stream << "{";
	stream << "\"sourcePath\":\"" << json_escape(source_path) << "\",";
	stream << "\"unitMeters\":" << scene->settings.unit_meters << ",";
	stream << "\"outputUnitMeters\":" << output_unit_meters(scene, load_profile::current) << ",";
	stream << "\"frameRate\":" << scene->settings.frames_per_second << ",";
	stream << "\"clips\":[";

	for (size_t index = 0; index < scene->anim_stacks.count; ++index) {
		if (index > 0) {
			stream << ",";
		}

		const auto *stack = scene->anim_stacks.data[index];
		const auto source_name = to_string(stack->name);
		const auto display_name = normalize_clip_name(source_name);
		const auto fps = scene->settings.frames_per_second > 0.0 ? scene->settings.frames_per_second : 30.0;
		const auto duration = std::max(0.0, stack->time_end - stack->time_begin);
		const auto frame_count = static_cast<int>(std::floor(duration * fps + 0.5) + 1.0);

		stream << "{";
		stream << "\"sourceName\":\"" << json_escape(source_name) << "\",";
		stream << "\"displayName\":\"" << json_escape(display_name) << "\",";
		stream << "\"timeBegin\":" << stack->time_begin << ",";
		stream << "\"timeEnd\":" << stack->time_end << ",";
		stream << "\"frameRate\":" << fps << ",";
		stream << "\"frameCount\":" << std::max(frame_count, 1);
		stream << "}";
	}

	stream << "]}";
	ufbx_free_scene(scene);
	return copy_result(stream.str());
}

extern "C" __declspec(dllexport) const char *ual2_sample_clip_json(const char *source_path, const char *clip_name, const char *target_path)
{
	return ual2_sample_clip_json_ex(source_path, clip_name, target_path, "Current", "Current");
}

extern "C" __declspec(dllexport) const char *ual2_sample_clip_json_ex(
	const char *source_path,
	const char *clip_name,
	const char *target_path,
	const char *source_profile_name,
	const char *target_profile_name)
{
	if (!source_path || !source_path[0]) {
		return copy_result(make_error_json("Source FBX path is empty"));
	}
	if (!clip_name || !clip_name[0]) {
		return copy_result(make_error_json("Clip name is empty"));
	}
	if (!target_path || !target_path[0]) {
		return copy_result(make_error_json("Target FBX path is empty"));
	}

	load_profile source_profile = load_profile::current;
	load_profile target_profile = load_profile::current;
	if (source_profile_name && source_profile_name[0] && !try_parse_profile(source_profile_name, source_profile)) {
		return copy_result(make_error_json(std::string("Unknown source profile '") + source_profile_name + "'"));
	}
	if (target_profile_name && target_profile_name[0] && !try_parse_profile(target_profile_name, target_profile)) {
		return copy_result(make_error_json(std::string("Unknown target profile '") + target_profile_name + "'"));
	}

	std::string error_text;
	ufbx_scene *source_scene = load_scene(source_path, source_profile, error_text);
	if (!source_scene) {
		return copy_result(make_error_json(error_text));
	}

	ufbx_scene *target_scene = load_scene(target_path, target_profile, error_text);
	if (!target_scene) {
		ufbx_free_scene(source_scene);
		return copy_result(make_error_json(error_text));
	}

	const auto *stack = find_clip(source_scene, clip_name);
	if (!stack) {
		ufbx_free_scene(source_scene);
		ufbx_free_scene(target_scene);
		return copy_result(make_error_json(std::string("Could not find clip '") + clip_name + "'"));
	}

	const auto fps = source_scene->settings.frames_per_second > 0.0 ? source_scene->settings.frames_per_second : 30.0;
	const auto duration = std::max(0.0, stack->time_end - stack->time_begin);
	const auto frame_count = std::max(1, static_cast<int>(std::floor(duration * fps + 0.5) + 1.0));

	const auto source_nodes = collect_included_nodes(source_scene);
	const auto target_nodes = collect_included_nodes(target_scene);

	std::ostringstream stream;
	stream << std::fixed << std::setprecision(6);
	stream << "{";
	stream << "\"sourcePath\":\"" << json_escape(source_path) << "\",";
	stream << "\"targetPath\":\"" << json_escape(target_path) << "\",";
	stream << "\"clipName\":\"" << json_escape(to_string(stack->name)) << "\",";
	stream << "\"displayName\":\"" << json_escape(normalize_clip_name(to_string(stack->name))) << "\",";
	stream << "\"sourceProfile\":\"" << profile_name(source_profile) << "\",";
	stream << "\"targetProfile\":\"" << profile_name(target_profile) << "\",";
	stream << "\"sourceUnitMeters\":" << source_scene->settings.unit_meters << ",";
	stream << "\"sourceOutputUnitMeters\":" << output_unit_meters(source_scene, source_profile) << ",";
	stream << "\"targetUnitMeters\":" << target_scene->settings.unit_meters << ",";
	stream << "\"targetOutputUnitMeters\":" << output_unit_meters(target_scene, target_profile) << ",";
	stream << "\"frameRate\":" << fps << ",";
	stream << "\"frameCount\":" << frame_count << ",";

	stream << "\"sourceBones\":[";
	std::unordered_map<uint32_t, ufbx_matrix> source_rest_cache;
	for (size_t index = 0; index < source_nodes.size(); ++index) {
		if (index > 0) {
			stream << ",";
		}

		const auto &included = source_nodes[index];
		auto world = evaluate_world_matrix(nullptr, included.node, 0.0, source_rest_cache);
		ufbx_matrix parent_world = ufbx_identity_matrix;
		const ufbx_matrix *parent_ptr = nullptr;
		if (included.included_parent) {
			parent_world = evaluate_world_matrix(nullptr, included.included_parent, 0.0, source_rest_cache);
			parent_ptr = &parent_world;
		}
		const auto inv_parent = parent_ptr ? ufbx_matrix_invert(parent_ptr) : ufbx_identity_matrix;
		const auto local_matrix = parent_ptr ? ufbx_matrix_mul(&inv_parent, &world) : world;
		const auto local_transform = ufbx_matrix_to_transform(&local_matrix);
		const auto local = make_transform(local_transform);

		stream << "{";
		stream << "\"name\":\"" << json_escape(included.name) << "\",";
		stream << "\"parentName\":\"" << json_escape(included.parent_name) << "\",";
		stream << "\"translation\":";
		append_transform_array(stream, local);
		stream << ",";
		stream << "\"rotation\":";
		append_rotation_array(stream, local);
		stream << "}";
	}
	stream << "],";

	stream << "\"targetBones\":[";
	std::unordered_map<uint32_t, ufbx_matrix> target_rest_cache;
	for (size_t index = 0; index < target_nodes.size(); ++index) {
		if (index > 0) {
			stream << ",";
		}

		const auto &included = target_nodes[index];
		auto world = evaluate_world_matrix(nullptr, included.node, 0.0, target_rest_cache);
		ufbx_matrix parent_world = ufbx_identity_matrix;
		const ufbx_matrix *parent_ptr = nullptr;
		if (included.included_parent) {
			parent_world = evaluate_world_matrix(nullptr, included.included_parent, 0.0, target_rest_cache);
			parent_ptr = &parent_world;
		}
		const auto inv_parent = parent_ptr ? ufbx_matrix_invert(parent_ptr) : ufbx_identity_matrix;
		const auto local_matrix = parent_ptr ? ufbx_matrix_mul(&inv_parent, &world) : world;
		const auto local_transform = ufbx_matrix_to_transform(&local_matrix);
		const auto local = make_transform(local_transform);

		stream << "{";
		stream << "\"name\":\"" << json_escape(included.name) << "\",";
		stream << "\"parentName\":\"" << json_escape(included.parent_name) << "\",";
		stream << "\"translation\":";
		append_transform_array(stream, local);
		stream << ",";
		stream << "\"rotation\":";
		append_rotation_array(stream, local);
		stream << "}";
	}
	stream << "],";

	stream << "\"frames\":[";
	for (int frame_index = 0; frame_index < frame_count; ++frame_index) {
		if (frame_index > 0) {
			stream << ",";
		}

		const auto relative_time = static_cast<double>(frame_index) / fps;
		const auto time = frame_index == frame_count - 1 ? stack->time_end : stack->time_begin + relative_time;
		std::unordered_map<uint32_t, ufbx_matrix> frame_cache;

		stream << "{";
		stream << "\"index\":" << frame_index << ",";
		stream << "\"time\":" << (time - stack->time_begin) << ",";
		stream << "\"translations\":[";
		for (size_t bone_index = 0; bone_index < source_nodes.size(); ++bone_index) {
			if (bone_index > 0) {
				stream << ",";
			}

			const auto &included = source_nodes[bone_index];
			const auto world = evaluate_world_matrix(stack->anim, included.node, time, frame_cache);
			ufbx_matrix parent_world = ufbx_identity_matrix;
			const ufbx_matrix *parent_ptr = nullptr;
			if (included.included_parent) {
				parent_world = evaluate_world_matrix(stack->anim, included.included_parent, time, frame_cache);
				parent_ptr = &parent_world;
			}
			const auto inv_parent = parent_ptr ? ufbx_matrix_invert(parent_ptr) : ufbx_identity_matrix;
			const auto local_matrix = parent_ptr ? ufbx_matrix_mul(&inv_parent, &world) : world;
			const auto local_transform = ufbx_matrix_to_transform(&local_matrix);
			const auto local = make_transform(local_transform);
			append_transform_array(stream, local);
		}
		stream << "],";

		stream << "\"rotations\":[";
		for (size_t bone_index = 0; bone_index < source_nodes.size(); ++bone_index) {
			if (bone_index > 0) {
				stream << ",";
			}

			const auto &included = source_nodes[bone_index];
			const auto world = evaluate_world_matrix(stack->anim, included.node, time, frame_cache);
			ufbx_matrix parent_world = ufbx_identity_matrix;
			const ufbx_matrix *parent_ptr = nullptr;
			if (included.included_parent) {
				parent_world = evaluate_world_matrix(stack->anim, included.included_parent, time, frame_cache);
				parent_ptr = &parent_world;
			}
			const auto inv_parent = parent_ptr ? ufbx_matrix_invert(parent_ptr) : ufbx_identity_matrix;
			const auto local_matrix = parent_ptr ? ufbx_matrix_mul(&inv_parent, &world) : world;
			const auto local_transform = ufbx_matrix_to_transform(&local_matrix);
			const auto local = make_transform(local_transform);
			append_rotation_array(stream, local);
		}
		stream << "]";
		stream << "}";
	}
	stream << "]";
	stream << "}";

	ufbx_free_scene(source_scene);
	ufbx_free_scene(target_scene);
	return copy_result(stream.str());
}

extern "C" __declspec(dllexport) void ual2_free_string(const char *pointer)
{
	if (pointer) {
		std::free(const_cast<char *>(pointer));
	}
}

extern "C" __declspec(dllexport) const char *ual2_inspect_scene_json(const char *scene_path, const char *profile_name_text)
{
	if (!scene_path || !scene_path[0]) {
		return copy_result(make_error_json("Scene path is empty"));
	}

	load_profile profile = load_profile::current;
	if (profile_name_text && profile_name_text[0] && !try_parse_profile(profile_name_text, profile)) {
		return copy_result(make_error_json(std::string("Unknown profile '") + profile_name_text + "'"));
	}

	std::string error_text;
	ufbx_scene *scene = load_scene(scene_path, profile, error_text);
	if (!scene) {
		return copy_result(make_error_json(error_text));
	}

	const auto included_nodes = collect_included_nodes(scene);
	std::unordered_map<uint32_t, ufbx_matrix> rest_cache;

	std::ostringstream stream;
	stream << std::fixed << std::setprecision(6);
	stream << "{";
	stream << "\"scenePath\":\"" << json_escape(scene_path) << "\",";
	stream << "\"profile\":\"" << profile_name(profile) << "\",";
	stream << "\"unitMeters\":" << scene->settings.unit_meters << ",";
	stream << "\"outputUnitMeters\":" << output_unit_meters(scene, profile) << ",";
	stream << "\"frameRate\":" << scene->settings.frames_per_second << ",";
	append_metadata_json(stream, scene->metadata);
	stream << ",";
	stream << "\"bones\":[";

	for (size_t index = 0; index < included_nodes.size(); ++index) {
		if (index > 0) {
			stream << ",";
		}

		const auto &included = included_nodes[index];
		const auto world = evaluate_world_matrix(nullptr, included.node, 0.0, rest_cache);
		ufbx_matrix parent_world = ufbx_identity_matrix;
		const ufbx_matrix *parent_ptr = nullptr;
		if (included.included_parent) {
			parent_world = evaluate_world_matrix(nullptr, included.included_parent, 0.0, rest_cache);
			parent_ptr = &parent_world;
		}
		const auto inv_parent = parent_ptr ? ufbx_matrix_invert(parent_ptr) : ufbx_identity_matrix;
		const auto local_matrix = parent_ptr ? ufbx_matrix_mul(&inv_parent, &world) : world;
		const auto local_transform = ufbx_matrix_to_transform(&local_matrix);
		const auto local = make_transform(local_transform);
		const auto world_transform = transform_from_matrix(world);

		stream << "{";
		stream << "\"name\":\"" << json_escape(included.name) << "\",";
		stream << "\"parentName\":\"" << json_escape(included.parent_name) << "\",";
		stream << "\"translation\":";
		append_transform_array(stream, local);
		stream << ",";
		stream << "\"rotation\":";
		append_rotation_array(stream, local);
		stream << ",";
		stream << "\"scale\":";
		append_scale_array(stream, local_transform.scale);
		stream << ",";
		stream << "\"worldTranslation\":";
		append_transform_array(stream, world_transform);
		stream << ",";
		stream << "\"worldRotation\":";
		append_rotation_array(stream, world_transform);
		stream << "}";
	}

	stream << "]";
	stream << "}";

	ufbx_free_scene(scene);
	return copy_result(stream.str());
}
