#include "xatlas_bridge.h"

#include "xatlas.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <mutex>
#include <new>
#include <exception>
#include <stdexcept>

namespace
{
	constexpr uint32_t kMaximumResolution = 16384;

	struct Budget;

	struct alignas(std::max_align_t) AllocationHeader
	{
		Budget *budget;
		size_t bytes;
		AllocationHeader *previous;
		AllocationHeader *next;
	};

	struct Budget
	{
		size_t limit;
		size_t used = 0;
		size_t external = 0;
		AllocationHeader *head = nullptr;
		bool exceeded = false;
	};

	class MemoryLimitExceeded final : public std::exception
	{
	public:
		const char *what() const noexcept override { return "The per-job memory limit was exceeded."; }
	};

	std::mutex g_jobMutex;
	std::mutex g_allocatorMutex;
	std::once_flag g_allocatorInstall;
	Budget *g_activeBudget = nullptr;

	void UnlinkAllocation(Budget *budget, AllocationHeader *header) noexcept
	{
		if (header->previous)
			header->previous->next = header->next;
		else
			budget->head = header->next;
		if (header->next)
			header->next->previous = header->previous;
	}

	void LinkAllocation(Budget *budget, AllocationHeader *header) noexcept
	{
		header->budget = budget;
		header->previous = nullptr;
		header->next = budget->head;
		if (budget->head)
			budget->head->previous = header;
		budget->head = header;
	}

	void *BoundedRealloc(void *pointer, size_t size)
	{
		std::lock_guard<std::mutex> lock(g_allocatorMutex);
		if (size == 0)
		{
			if (!pointer)
				return nullptr;
			auto *header = static_cast<AllocationHeader *>(pointer) - 1;
			Budget *budget = header->budget;
			UnlinkAllocation(budget, header);
			budget->used -= header->bytes;
			std::free(header);
			return nullptr;
		}

		Budget *budget = g_activeBudget;
		if (!budget || size > std::numeric_limits<size_t>::max() - sizeof(AllocationHeader))
			throw std::bad_alloc();

		auto *oldHeader = pointer ? static_cast<AllocationHeader *>(pointer) - 1 : nullptr;
		const size_t oldBytes = oldHeader ? oldHeader->bytes : 0;
		if (oldHeader && oldHeader->budget != budget)
			throw std::bad_alloc();

		const size_t newBytes = sizeof(AllocationHeader) + size;
		if (newBytes > budget->limit || budget->used - oldBytes > budget->limit - newBytes)
		{
			budget->exceeded = true;
			throw MemoryLimitExceeded();
		}

		AllocationHeader *previous = oldHeader ? oldHeader->previous : nullptr;
		AllocationHeader *next = oldHeader ? oldHeader->next : nullptr;
		auto *newHeader = static_cast<AllocationHeader *>(std::realloc(oldHeader, newBytes));
		if (!newHeader)
			throw std::bad_alloc();

		newHeader->bytes = newBytes;
		if (oldHeader)
		{
			newHeader->budget = budget;
			newHeader->previous = previous;
			newHeader->next = next;
			if (previous)
				previous->next = newHeader;
			else
				budget->head = newHeader;
			if (next)
				next->previous = newHeader;
			budget->used = budget->used - oldBytes + newBytes;
		}
		else
		{
			LinkAllocation(budget, newHeader);
			budget->used += newBytes;
		}
		return newHeader + 1;
	}

	void BoundedFree(void *pointer) noexcept
	{
		if (!pointer)
			return;
		try
		{
			std::lock_guard<std::mutex> lock(g_allocatorMutex);
			auto *header = static_cast<AllocationHeader *>(pointer) - 1;
			Budget *budget = header->budget;
			UnlinkAllocation(budget, header);
			budget->used -= header->bytes;
			std::free(header);
		}
		catch (...)
		{
			// This callback must never unwind into xatlas.
		}
	}

	void InstallAllocator()
	{
		std::call_once(g_allocatorInstall, [] { xatlas::SetAlloc(BoundedRealloc, BoundedFree); });
	}

	void ActivateBudget(Budget *budget)
	{
		std::lock_guard<std::mutex> lock(g_allocatorMutex);
		g_activeBudget = budget;
	}

	void DeactivateBudget(Budget *budget) noexcept
	{
		try
		{
			std::lock_guard<std::mutex> lock(g_allocatorMutex);
			if (g_activeBudget == budget)
				g_activeBudget = nullptr;
		}
		catch (...)
		{
		}
	}

	void FreeRemainder(Budget *budget) noexcept
	{
		try
		{
			std::lock_guard<std::mutex> lock(g_allocatorMutex);
			while (budget->head)
			{
				AllocationHeader *header = budget->head;
				UnlinkAllocation(budget, header);
				budget->used -= header->bytes;
				std::free(header);
			}
		}
		catch (...)
		{
		}
	}

	bool ReserveExternal(Budget *budget, size_t bytes) noexcept
	{
		try
		{
			std::lock_guard<std::mutex> lock(g_allocatorMutex);
			if (bytes > budget->limit || budget->used > budget->limit - bytes)
			{
				budget->exceeded = true;
				return false;
			}
			budget->used += bytes;
			budget->external += bytes;
			return true;
		}
		catch (...)
		{
			return false;
		}
	}

	void ReleaseExternal(Budget *budget, size_t bytes) noexcept
	{
		try
		{
			std::lock_guard<std::mutex> lock(g_allocatorMutex);
			budget->used -= bytes;
			budget->external -= bytes;
		}
		catch (...)
		{
		}
	}

	void WriteError(char *buffer, uint64_t capacity, const char *message) noexcept
	{
		if (!buffer || capacity == 0)
			return;
		const size_t safeCapacity = capacity > std::numeric_limits<size_t>::max()
			? std::numeric_limits<size_t>::max()
			: static_cast<size_t>(capacity);
		if (safeCapacity == 0)
			return;
		std::snprintf(buffer, safeCapacity, "%s", message ? message : "");
		buffer[safeCapacity - 1] = '\0';
	}

	struct ProgressState
	{
		DeviantXatlasProgressCallback callback = nullptr;
		void *userData = nullptr;
		std::atomic<bool> cancelled{false};
		std::atomic<bool> callbackFailed{false};
	};

	bool ProgressThunk(xatlas::ProgressCategory category, int progress, void *userData) noexcept
	{
		auto *state = static_cast<ProgressState *>(userData);
		if (!state || !state->callback)
			return true;
		if (state->cancelled.load(std::memory_order_relaxed))
			return false;
		try
		{
			if (state->callback(static_cast<int32_t>(category), static_cast<int32_t>(progress), state->userData) == 0)
			{
				state->cancelled.store(true, std::memory_order_relaxed);
				return false;
			}
			return true;
		}
		catch (...)
		{
			state->callbackFailed.store(true, std::memory_order_relaxed);
			state->cancelled.store(true, std::memory_order_relaxed);
			return false;
		}
	}

	DeviantXatlasStatus Fail(DeviantXatlasStatus status, char *error, uint64_t errorCapacity, const char *message) noexcept
	{
		WriteError(error, errorCapacity, message);
		return status;
	}

	bool CheckedResultBytes(uint64_t cornerCount, size_t *uvBytes, size_t *totalBytes) noexcept
	{
		if (cornerCount > std::numeric_limits<size_t>::max() / (2 * sizeof(float)))
			return false;
		*uvBytes = static_cast<size_t>(cornerCount) * 2 * sizeof(float);
		if (*uvBytes > std::numeric_limits<size_t>::max() - sizeof(DeviantXatlasResult))
			return false;
		*totalBytes = sizeof(DeviantXatlasResult) + *uvBytes;
		return true;
	}

	DeviantXatlasStatus ValidateInput(
		const DeviantXatlasInput *input,
		const DeviantXatlasOptions *options,
		char *error,
		uint64_t errorCapacity) noexcept
	{
		if (!input || !options)
			return Fail(DEVIANT_XATLAS_INVALID_ARGUMENT, error, errorCapacity, "Input and options are required.");
		if (!input->positions_xyz || input->vertex_count < 3 || input->vertex_count > UINT32_MAX)
			return Fail(DEVIANT_XATLAS_INVALID_ARGUMENT, error, errorCapacity, "A position array and at least three vertices are required.");
		if (!input->corner_indices || input->corner_count < 3 || input->corner_count > UINT32_MAX || input->corner_count % 3 != 0)
			return Fail(DEVIANT_XATLAS_INVALID_ARGUMENT, error, errorCapacity, "Triangle corner indices must be present and divisible by three.");
		if ((input->normals_xyz == nullptr) != (input->normal_vertex_count == 0) ||
			(input->normals_xyz && input->normal_vertex_count != input->vertex_count))
			return Fail(DEVIANT_XATLAS_INVALID_ARGUMENT, error, errorCapacity, "Normals must be absent or contain one XYZ value per vertex.");
		if ((input->source_uvs == nullptr) != (input->source_uv_vertex_count == 0) ||
			(input->source_uvs && input->source_uv_vertex_count != input->vertex_count))
			return Fail(DEVIANT_XATLAS_INVALID_ARGUMENT, error, errorCapacity, "Source UVs must be absent or contain one UV per vertex.");
		if (options->resolution == 0 || options->resolution > kMaximumResolution || options->padding >= options->resolution)
			return Fail(DEVIANT_XATLAS_INVALID_ARGUMENT, error, errorCapacity, "Resolution must be in [1, 16384] and padding must be smaller than resolution.");
		if (options->memory_limit_bytes == 0 || options->memory_limit_bytes > std::numeric_limits<size_t>::max())
			return Fail(DEVIANT_XATLAS_INVALID_ARGUMENT, error, errorCapacity, "A finite positive per-job memory limit is required.");
		if (input->vertex_count > std::numeric_limits<size_t>::max() / (3 * sizeof(float)) ||
			input->corner_count > std::numeric_limits<size_t>::max() / sizeof(uint32_t))
			return Fail(DEVIANT_XATLAS_INVALID_ARGUMENT, error, errorCapacity, "Input counts exceed the addressable range.");

		const uint64_t positionFloats = input->vertex_count * 3;
		for (uint64_t i = 0; i < positionFloats; i++)
		{
			if (!std::isfinite(input->positions_xyz[i]))
				return Fail(DEVIANT_XATLAS_INVALID_ARGUMENT, error, errorCapacity, "Positions must be finite.");
		}
		if (input->normals_xyz)
		{
			for (uint64_t i = 0; i < positionFloats; i++)
			{
				if (!std::isfinite(input->normals_xyz[i]))
					return Fail(DEVIANT_XATLAS_INVALID_ARGUMENT, error, errorCapacity, "Normals must be finite.");
			}
		}
		if (input->source_uvs)
		{
			const uint64_t uvFloats = input->vertex_count * 2;
			for (uint64_t i = 0; i < uvFloats; i++)
			{
				if (!std::isfinite(input->source_uvs[i]))
					return Fail(DEVIANT_XATLAS_INVALID_ARGUMENT, error, errorCapacity, "Source UVs must be finite.");
			}
		}
		for (uint64_t i = 0; i < input->corner_count; i++)
		{
			if (input->corner_indices[i] >= input->vertex_count)
				return Fail(DEVIANT_XATLAS_INVALID_ARGUMENT, error, errorCapacity, "A corner index is outside the position array.");
		}
		return DEVIANT_XATLAS_OK;
	}

	DeviantXatlasStatus RunJob(
		const DeviantXatlasInput *input,
		const DeviantXatlasOptions *options,
		DeviantXatlasProgressCallback progressCallback,
		void *progressUserData,
		DeviantXatlasResult **result,
		char *error,
		uint64_t errorCapacity)
	{
		const DeviantXatlasStatus validation = ValidateInput(input, options, error, errorCapacity);
		if (validation != DEVIANT_XATLAS_OK)
			return validation;

		std::unique_lock<std::mutex> serial(g_jobMutex);
		InstallAllocator();

		ProgressState progress;
		progress.callback = progressCallback;
		progress.userData = progressUserData;

		Budget budget;
		budget.limit = static_cast<size_t>(options->memory_limit_bytes);
		ActivateBudget(&budget);

		xatlas::Atlas *atlas = nullptr;
		DeviantXatlasResult *output = nullptr;
		size_t reservedOutputBytes = 0;
		try
		{
			atlas = xatlas::Create();
			if (!atlas)
				throw std::bad_alloc();

			xatlas::SetProgressCallback(atlas, progressCallback ? ProgressThunk : nullptr, &progress);
			xatlas::MeshDecl declaration;
			declaration.vertexPositionData = input->positions_xyz;
			declaration.vertexPositionStride = sizeof(float) * 3;
			declaration.vertexCount = static_cast<uint32_t>(input->vertex_count);
			declaration.indexData = input->corner_indices;
			declaration.indexCount = static_cast<uint32_t>(input->corner_count);
			declaration.indexFormat = xatlas::IndexFormat::UInt32;
			if (input->normals_xyz)
			{
				declaration.vertexNormalData = input->normals_xyz;
				declaration.vertexNormalStride = sizeof(float) * 3;
			}
			if (input->source_uvs)
			{
				declaration.vertexUvData = input->source_uvs;
				declaration.vertexUvStride = sizeof(float) * 2;
			}

			const xatlas::AddMeshError addError = xatlas::AddMesh(atlas, declaration, 1);
			if (addError != xatlas::AddMeshError::Success)
			{
				const char *reason = xatlas::StringForEnum(addError);
				xatlas::Destroy(atlas);
				atlas = nullptr;
				DeactivateBudget(&budget);
				FreeRemainder(&budget);
				return Fail(DEVIANT_XATLAS_UPSTREAM_FAILURE, error, errorCapacity, reason);
			}
			xatlas::AddMeshJoin(atlas);
			if (progress.callbackFailed.load(std::memory_order_relaxed))
				throw std::runtime_error("The progress callback failed.");
			if (progress.cancelled.load(std::memory_order_relaxed))
			{
				xatlas::Destroy(atlas);
				atlas = nullptr;
				DeactivateBudget(&budget);
				FreeRemainder(&budget);
				return Fail(DEVIANT_XATLAS_CANCELLED, error, errorCapacity, "Atlas generation was cancelled.");
			}

			xatlas::PackOptions packOptions;
			packOptions.resolution = options->resolution;
			packOptions.padding = options->padding;
			packOptions.createImage = false;
            xatlas::ChartOptions chartOptions;
            chartOptions.useInputMeshUvs = false;
            if (input->source_uvs) chartOptions.textureSeamWeight = 1000.0f;
            xatlas::Generate(atlas, chartOptions, packOptions);
			if (progress.callbackFailed.load(std::memory_order_relaxed))
				throw std::runtime_error("The progress callback failed.");
			if (progress.cancelled.load(std::memory_order_relaxed))
			{
				xatlas::Destroy(atlas);
				atlas = nullptr;
				DeactivateBudget(&budget);
				FreeRemainder(&budget);
				return Fail(DEVIANT_XATLAS_CANCELLED, error, errorCapacity, "Atlas generation was cancelled.");
			}
			if (atlas->atlasCount != 1)
			{
				xatlas::Destroy(atlas);
				atlas = nullptr;
				DeactivateBudget(&budget);
				FreeRemainder(&budget);
				return Fail(DEVIANT_XATLAS_ATLAS_COUNT_MISMATCH, error, errorCapacity, "xatlas did not produce exactly one atlas.");
			}
			if (!atlas->width || !atlas->height || atlas->meshCount != 1 || !atlas->meshes)
				throw std::runtime_error("xatlas returned an incomplete atlas.");

			const xatlas::Mesh &mesh = atlas->meshes[0];
			for (uint32_t chart = 0; chart < mesh.chartCount; chart++)
				if (mesh.chartArray[chart].type == xatlas::ChartType::Invalid)
					throw std::runtime_error("xatlas returned an invalid chart; the source faces need separate charts.");
			if (mesh.indexCount != input->corner_count || !mesh.indexArray || !mesh.vertexArray)
			{
				xatlas::Destroy(atlas);
				atlas = nullptr;
				DeactivateBudget(&budget);
				FreeRemainder(&budget);
				return Fail(DEVIANT_XATLAS_CORRESPONDENCE_MISMATCH, error, errorCapacity, "xatlas changed the input corner count.");
			}

			for (uint64_t corner = 0; corner < input->corner_count; corner++)
			{
				const uint32_t outputIndex = mesh.indexArray[corner];
				if (outputIndex >= mesh.vertexCount)
				{
					xatlas::Destroy(atlas);
					atlas = nullptr;
					DeactivateBudget(&budget);
					FreeRemainder(&budget);
					return Fail(DEVIANT_XATLAS_INVALID_OUTPUT, error, errorCapacity, "xatlas returned an out-of-range output corner index.");
				}
				const xatlas::Vertex &vertex = mesh.vertexArray[outputIndex];
				if (vertex.xref != input->corner_indices[corner] || vertex.atlasIndex != 0)
				{
                    char mismatch[256];
                    std::snprintf(mismatch, sizeof(mismatch), "Corner %llu: expected source %u, got %u, atlas %d, chart %d.",
                        static_cast<unsigned long long>(corner), input->corner_indices[corner], vertex.xref, vertex.atlasIndex, vertex.chartIndex);
					xatlas::Destroy(atlas);
					atlas = nullptr;
					DeactivateBudget(&budget);
					FreeRemainder(&budget);
                    return Fail(DEVIANT_XATLAS_CORRESPONDENCE_MISMATCH, error, errorCapacity, mismatch);
				}
				if (!std::isfinite(vertex.uv[0]) || !std::isfinite(vertex.uv[1]))
				{
					xatlas::Destroy(atlas);
					atlas = nullptr;
					DeactivateBudget(&budget);
					FreeRemainder(&budget);
					return Fail(DEVIANT_XATLAS_INVALID_OUTPUT, error, errorCapacity, "xatlas returned a nonfinite UV coordinate.");
				}
				if (vertex.uv[0] < 0.0f || vertex.uv[0] > static_cast<float>(atlas->width) ||
					vertex.uv[1] < 0.0f || vertex.uv[1] > static_cast<float>(atlas->height))
				{
					xatlas::Destroy(atlas);
					atlas = nullptr;
					DeactivateBudget(&budget);
					FreeRemainder(&budget);
					return Fail(DEVIANT_XATLAS_INVALID_OUTPUT, error, errorCapacity, "xatlas returned a UV coordinate outside the atlas dimensions.");
				}
			}

			size_t uvBytes = 0;
			size_t totalBytes = 0;
			if (!CheckedResultBytes(input->corner_count, &uvBytes, &totalBytes))
				throw std::bad_alloc();
			if (!ReserveExternal(&budget, totalBytes))
				throw MemoryLimitExceeded();
			reservedOutputBytes = totalBytes;
			output = static_cast<DeviantXatlasResult *>(std::calloc(1, sizeof(DeviantXatlasResult)));
			if (!output)
				throw std::bad_alloc();
			output->corner_uvs = static_cast<float *>(std::malloc(uvBytes));
			if (!output->corner_uvs)
				throw std::bad_alloc();
			output->corner_count = input->corner_count;
			output->width = atlas->width;
			output->height = atlas->height;
			for (uint64_t corner = 0; corner < input->corner_count; corner++)
			{
				const xatlas::Vertex &vertex = mesh.vertexArray[mesh.indexArray[corner]];
				output->corner_uvs[corner * 2] = vertex.uv[0] / static_cast<float>(atlas->width);
				output->corner_uvs[corner * 2 + 1] = vertex.uv[1] / static_cast<float>(atlas->height);
			}

			xatlas::Destroy(atlas);
			atlas = nullptr;
			DeactivateBudget(&budget);
			FreeRemainder(&budget);
			*result = output;
			output = nullptr;
			reservedOutputBytes = 0;
			WriteError(error, errorCapacity, "");
			return DEVIANT_XATLAS_OK;
		}
		catch (...)
		{
			if (atlas)
			{
				try { xatlas::Destroy(atlas); } catch (...) { }
				atlas = nullptr;
			}
			DeactivateBudget(&budget);
			FreeRemainder(&budget);
			if (output)
			{
				std::free(output->corner_uvs);
				std::free(output);
			}
			if (reservedOutputBytes)
				ReleaseExternal(&budget, reservedOutputBytes);
			throw;
		}
	}
}

extern "C" DEVIANT_XATLAS_API DeviantXatlasStatus deviant_xatlas_generate(
	const DeviantXatlasInput *input,
	const DeviantXatlasOptions *options,
	DeviantXatlasProgressCallback progress_callback,
	void *progress_user_data,
	DeviantXatlasResult **result,
	char *error_buffer,
	uint64_t error_buffer_capacity) noexcept
{
	if (result)
		*result = nullptr;
	WriteError(error_buffer, error_buffer_capacity, "");
	if (!result)
		return Fail(DEVIANT_XATLAS_INVALID_ARGUMENT, error_buffer, error_buffer_capacity, "A result output pointer is required.");

	try
	{
		return RunJob(input, options, progress_callback, progress_user_data, result, error_buffer, error_buffer_capacity);
	}
	catch (const MemoryLimitExceeded &exception)
	{
		return Fail(DEVIANT_XATLAS_MEMORY_LIMIT, error_buffer, error_buffer_capacity, exception.what());
	}
	catch (const std::bad_alloc &)
	{
		return Fail(DEVIANT_XATLAS_ALLOCATION_FAILURE, error_buffer, error_buffer_capacity, "Allocation failed within the configured memory limit.");
	}
	catch (const std::exception &exception)
	{
		return Fail(DEVIANT_XATLAS_INTERNAL_ERROR, error_buffer, error_buffer_capacity, exception.what());
	}
	catch (...)
	{
		return Fail(DEVIANT_XATLAS_INTERNAL_ERROR, error_buffer, error_buffer_capacity, "An unknown native error occurred.");
	}
}

extern "C" DEVIANT_XATLAS_API void deviant_xatlas_free_result(DeviantXatlasResult *result) noexcept
{
	if (!result)
		return;
	std::free(result->corner_uvs);
	std::free(result);
}

extern "C" DEVIANT_XATLAS_API const char *deviant_xatlas_status_string(DeviantXatlasStatus status) noexcept
{
	switch (status)
	{
	case DEVIANT_XATLAS_OK: return "success";
	case DEVIANT_XATLAS_INVALID_ARGUMENT: return "invalid argument";
	case DEVIANT_XATLAS_MEMORY_LIMIT: return "memory limit exceeded";
	case DEVIANT_XATLAS_ALLOCATION_FAILURE: return "allocation failure";
	case DEVIANT_XATLAS_CANCELLED: return "cancelled";
	case DEVIANT_XATLAS_UPSTREAM_FAILURE: return "xatlas rejected the mesh";
	case DEVIANT_XATLAS_ATLAS_COUNT_MISMATCH: return "xatlas generated a different atlas count";
	case DEVIANT_XATLAS_CORRESPONDENCE_MISMATCH: return "corner correspondence changed";
	case DEVIANT_XATLAS_INVALID_OUTPUT: return "xatlas returned invalid output";
	case DEVIANT_XATLAS_INTERNAL_ERROR: return "internal native error";
	default: return "unknown status";
	}
}
