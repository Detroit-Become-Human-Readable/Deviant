#ifndef DEVIANT_XATLAS_BRIDGE_H
#define DEVIANT_XATLAS_BRIDGE_H

#include <stdint.h>

#if defined(_WIN32)
#if defined(DEVIANT_XATLAS_BRIDGE_BUILD)
#define DEVIANT_XATLAS_API __declspec(dllexport)
#else
#define DEVIANT_XATLAS_API __declspec(dllimport)
#endif
#elif defined(__GNUC__) || defined(__clang__)
#define DEVIANT_XATLAS_API __attribute__((visibility("default")))
#else
#define DEVIANT_XATLAS_API
#endif

#ifdef __cplusplus
#define DEVIANT_XATLAS_NOEXCEPT noexcept
extern "C" {
#else
#define DEVIANT_XATLAS_NOEXCEPT
#endif

typedef enum DeviantXatlasStatus
{
	DEVIANT_XATLAS_OK = 0,
	DEVIANT_XATLAS_INVALID_ARGUMENT = 1,
	DEVIANT_XATLAS_MEMORY_LIMIT = 2,
	DEVIANT_XATLAS_ALLOCATION_FAILURE = 3,
	DEVIANT_XATLAS_CANCELLED = 4,
	DEVIANT_XATLAS_UPSTREAM_FAILURE = 5,
	DEVIANT_XATLAS_ATLAS_COUNT_MISMATCH = 6,
	DEVIANT_XATLAS_CORRESPONDENCE_MISMATCH = 7,
	DEVIANT_XATLAS_INVALID_OUTPUT = 8,
	DEVIANT_XATLAS_INTERNAL_ERROR = 9
} DeviantXatlasStatus;

typedef struct DeviantXatlasInput
{
	const float *positions_xyz;
	uint64_t vertex_count;
	/* Optional per-vertex XYZ values; the count must be zero when absent. */
	const float *normals_xyz;
	uint64_t normal_vertex_count;
	/* Optional per-vertex UV hints; the count must be zero when absent. */
	const float *source_uvs;
	uint64_t source_uv_vertex_count;
	/* Triangle-list corner indices; corner_count must be a nonzero multiple of three. */
	const uint32_t *corner_indices;
	uint64_t corner_count;
} DeviantXatlasInput;

typedef struct DeviantXatlasOptions
{
	uint32_t resolution;
	uint32_t padding;
	/* Hard ceiling for xatlas allocations plus the returned result buffers. */
	uint64_t memory_limit_bytes;
} DeviantXatlasOptions;

typedef struct DeviantXatlasResult
{
	uint64_t corner_count;
	uint32_t width;
	uint32_t height;
	float *corner_uvs; /* Two normalized floats per input corner, in input order. */
} DeviantXatlasResult;

/* Return nonzero to continue, zero to cancel. Categories are xatlas progress enum values. */
typedef int32_t (*DeviantXatlasProgressCallback)(int32_t category, int32_t progress, void *user_data);

/* Input arrays remain caller-owned and must contain the element counts specified above. */
DEVIANT_XATLAS_API DeviantXatlasStatus deviant_xatlas_generate(
	const DeviantXatlasInput *input,
	const DeviantXatlasOptions *options,
	DeviantXatlasProgressCallback progress_callback,
	void *progress_user_data,
	DeviantXatlasResult **result,
	char *error_buffer,
	uint64_t error_buffer_capacity) DEVIANT_XATLAS_NOEXCEPT;

/* Releases a result returned by deviant_xatlas_generate. */
DEVIANT_XATLAS_API void deviant_xatlas_free_result(DeviantXatlasResult *result) DEVIANT_XATLAS_NOEXCEPT;

DEVIANT_XATLAS_API const char *deviant_xatlas_status_string(DeviantXatlasStatus status) DEVIANT_XATLAS_NOEXCEPT;

#ifdef __cplusplus
}
#endif

#endif
