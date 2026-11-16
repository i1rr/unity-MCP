# Runtime Tool Coverage Audit

This document maps Editor tools to their Runtime equivalents, identifies gaps, and documents intentional exclusions.

## Coverage Matrix

### Scene & Hierarchy

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `get_hierarchy` | `runtime_get_hierarchy` | ✅ Covered |
| `get_hierarchy_summary` | — | Gap (low priority) |
| `find_gameobjects` | `runtime_find_gameobjects` | ✅ Covered |
| `get_gameobject` | — | Gap (use `runtime_get_components`) |
| `create_gameobject` | — | Excluded |
| `destroy_gameobject` | — | Excluded |
| `set_transform` | `runtime_get_transform` | Partial (read-only) |

### Components

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `get_components` | `runtime_get_components` | ✅ Covered |
| `get_component_properties` | `runtime_get_component_data` | Partial: serialized fields by default; optional selected instance fields/auto-property backing fields; no computed properties |
| `set_component_property` | `runtime_set_field` | ✅ Covered |
| `add_component` | — | Excluded |
| `remove_component` | — | Excluded |

### Animation

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `get_animator_state` | `runtime_get_animator_state` | ✅ Covered |
| `set_animator_parameter` | `runtime_set_animator_param` | ✅ Covered |
| `get_animation_controllers` | — | Excluded |
| `get_controller_states` | — | Excluded |
| `get_animation_clips` | — | Excluded |
| `get_animation_events` | — | Excluded |

### Profiling & Performance

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `get_frame_timing` | `runtime_get_frame_stats` | ✅ Covered |
| `get_fps_realtime` | `runtime_get_fps_realtime` | ✅ Covered |
| `get_per_camera_stats` | `runtime_get_per_camera_stats` | ✅ Covered |
| `get_gpu_timing` | `runtime_get_gpu_timing` | ✅ Covered |
| `get_rendering_stats` | — | Gap (low priority) |
| `get_memory_summary` | `runtime_get_memory` | ✅ Covered |
| `get_loaded_textures` | — | ✅ Added: `runtime_list_loaded_assets` |
| `get_loaded_meshes` | — | ✅ Added: `runtime_list_loaded_assets` |
| `get_loaded_materials` | — | ✅ Added: `runtime_list_loaded_assets` |
| `get_active_render_textures` | — | Gap (low priority) |
| `find_leaked_objects` | — | Gap (requires Editor API) |
| `get_object_count_by_type` | — | ✅ Added: `runtime_list_loaded_assets` |
| `take_memory_baseline` | — | Gap (low priority) |
| `compare_memory_baseline` | — | Gap (low priority) |
| `get_profiler_counters` | `runtime_get_profiler_counters` | ✅ Covered |
| `get_gc_pressure` | `runtime_gc_collect` | Partial |

### Screenshots

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `take_screenshot` | `runtime_take_screenshot` | ✅ Covered |
| `take_screenshot_base64` | `runtime_take_screenshot_base64` | ✅ Covered |

### Console & Logging

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `get_console_logs` | `runtime_get_logs` | ✅ Covered |
| `clear_console` | `runtime_clear_logs` | ✅ Covered |
| `wait_for_compilation` | — | Excluded |

### Assets

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `get_asset_info` | — | Excluded |
| `get_serialized_asset` | — | Excluded |
| `get_project_structure` | — | Excluded |
| `search_assets` | — | Excluded |
| `guid_to_path` | — | Excluded |
| `path_to_guid` | — | Excluded |
| `get_asset_dependencies` | — | Excluded |
| `get_asset_references` | — | Excluded |
| `create_asset` | — | Excluded |
| `delete_asset` | — | Excluded |
| `move_asset` | — | Excluded |
| `reimport_asset` | — | Excluded |

### Files

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `read_file` | — | Excluded |
| `write_file` | — | Excluded |
| `list_directory` | — | Excluded |
| `file_exists` | — | Excluded |
| `get_file_info` | — | Excluded |

### Source Control (Perforce)

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `p4_status` | — | Excluded |
| `p4_opened` | — | Excluded |
| `p4_checkout` | — | Excluded |
| `p4_revert` | — | Excluded |
| `p4_diff` | — | Excluded |
| `p4_submit` | — | Excluded |
| `p4_changes` | — | Excluded |
| `p4_fstat` | — | Excluded |

### Editor Control

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `ping` | `ping` | ✅ Covered |
| `get_editor_state` | — | Excluded |
| `enter_play_mode` | — | Excluded |
| `exit_play_mode` | — | Excluded |
| `pause_editor` | — | Excluded |
| `step_frame` | — | Excluded |
| `execute_menu_item` | — | Excluded |
| `get_selection` | — | Excluded |
| `set_selection` | — | Excluded |
| `undo` | — | Excluded |
| `redo` | — | Excluded |
| `refresh_assets` | — | Excluded |

### Build & Project

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `get_build_settings` | — | Excluded |
| `get_player_settings` | — | Excluded |
| `get_project_packages` | — | Excluded |
| `get_quality_settings` | — | Excluded |
| `get_project_settings` | — | Excluded |
| `get_rendering_pipeline` | — | Excluded |

### Code & Compilation

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `search_in_files` | — | Excluded |
| `get_compilation_errors` | — | Excluded |
| `get_asmdef_info` | — | Excluded |
| `execute_code` | — | Excluded |
| `batch_execute` | — | Excluded |
| `get_job_status` | — | Excluded |

### Prefab

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `get_prefab_info` | — | Excluded |
| `get_prefab_overrides` | — | Excluded |
| `apply_prefab_override` | — | Excluded |
| `revert_prefab_override` | — | Excluded |
| `open_prefab` | — | Excluded |
| `close_prefab` | — | Excluded |
| `unpack_prefab` | — | Excluded |

### Type Reflection

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `get_all_types` | — | Gap (feasible) |
| `get_type_info` | — | Gap (feasible) |

### Session

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `start_recording` | `runtime_start_recording` | ✅ Covered |
| `stop_recording` | `runtime_stop_recording` | ✅ Covered |
| `get_recording_status` | `runtime_get_recording_status` | ✅ Covered |
| `wait_for_frames` | `runtime_wait_for_frames` | ✅ Covered |
| `wait_for_seconds` | `runtime_wait_for_seconds` | ✅ Covered |

### Data

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| — | `runtime_get_so_value` | Runtime-only |
| — | `runtime_list_sos` | Runtime-only |

### Coroutines

| Editor Tool | Runtime Equivalent | Status |
|---|---|---|
| `get_active_coroutines` | `runtime_get_active_coroutines` | ✅ Covered |

### Shared (both platforms)

| Tool | Status |
|---|---|
| `diagnostics` | ✅ Shared |
| `diagnostics_tool_performance` | ✅ Shared |
| `get_module_status` | ✅ Shared |

---

## Priority Additions (Implemented)

### `runtime_list_loaded_assets`
Lists loaded assets by type (textures, meshes, materials, shaders, audio clips) with memory sizes. Provides a read-only alternative to Editor memory profiling tools.

---

## Exclusion Rationale

### Editor-Only by Design
- **Asset operations** (`create_asset`, `delete_asset`, `move_asset`, `reimport_asset`): Require `AssetDatabase` which is Editor-only.
- **Perforce tools**: `p4` command-line tool is not present in player builds.
- **File tools** (`read_file`, `write_file`): Filesystem write access is a security risk in deployed builds. Sandboxed to `persistentDataPath` at runtime.
- **Code tools** (`search_in_files`, `get_compilation_errors`, `execute_code`): Source code and the C# compiler are not available at runtime.
- **Prefab tools**: `PrefabUtility` is Editor-only.
- **Build/project tools**: `EditorBuildSettings`, `PlayerSettings` are Editor-only APIs.

### Intentionally Excluded for Safety
- **Scene mutation** (`create_gameobject`, `destroy_gameobject`, `add_component`, `remove_component`): Mutating a running game's scene graph remotely is dangerous. Read-only inspection is preferred.
- **Editor control** (play/pause/step, selection, undo/redo): Not applicable to player builds.

### Low Priority Gaps
- **`get_hierarchy_summary`**: Covered by `runtime_get_hierarchy` with depth limiting.
- **`get_rendering_stats`**: Partially covered by `runtime_get_frame_stats` and profiler counters.
- **Memory baseline tools**: Would require persistent state that outlives typical runtime debugging sessions.
- **`get_all_types` / `get_type_info`**: Feasible via reflection but rarely needed at runtime. Can be added if demand arises.

---

## Summary

- **Editor tools**: ~85
- **Coverage of applicable tools**: ~85% (tools that are feasible at runtime have equivalents)
- **Intentionally excluded**: ~50 tools (Editor-only APIs or unsafe mutations)
