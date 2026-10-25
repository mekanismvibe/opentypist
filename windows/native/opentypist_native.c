// Small C shim next to llama.dll. The C# side calls llama.h directly for
// every function with plain arguments; this file covers the ones that take or
// return structs by value (model/context params, llama_batch), whose layout
// changes between llama.cpp releases, plus two vector loops over the logits.

#include "llama.h"
#include "ggml-backend.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

#define OT_API __declspec(dllexport)

static void quiet_log(enum ggml_log_level level, const char * text, void * user_data) {
    (void) level;
    (void) text;
    (void) user_data;
}

// Loads ggml's backend DLLs from `backend_dir` only (never the current
// directory), then initializes llama.cpp. Returns the number of devices.
OT_API int32_t ot_init(const char * backend_dir, int32_t quiet) {
    static int initialized = 0;
    if (quiet) {
        llama_log_set(quiet_log, NULL);
    }
    if (!initialized) {
        initialized = 1;
        ggml_backend_load_all_from_path(backend_dir);
        llama_backend_init();
    }
    return (int32_t) ggml_backend_dev_count();
}

// Describes device `index` as "name|description|type|free MiB|total MiB".
OT_API int32_t ot_device_info(int32_t index, char * buf, int32_t buf_size) {
    if (index < 0 || (size_t) index >= ggml_backend_dev_count() || buf_size <= 0) {
        return -1;
    }
    ggml_backend_dev_t dev = ggml_backend_dev_get((size_t) index);
    size_t free_mem = 0, total_mem = 0;
    ggml_backend_dev_memory(dev, &free_mem, &total_mem);
    const char * type = "other";
    switch (ggml_backend_dev_type(dev)) {
        case GGML_BACKEND_DEVICE_TYPE_CPU:  type = "cpu";  break;
        case GGML_BACKEND_DEVICE_TYPE_GPU:  type = "gpu";  break;
        case GGML_BACKEND_DEVICE_TYPE_IGPU: type = "igpu"; break;
        default: break;
    }
    return snprintf(buf, (size_t) buf_size, "%s|%s|%s|%llu|%llu",
                    ggml_backend_dev_name(dev), ggml_backend_dev_description(dev), type,
                    (unsigned long long) (free_mem >> 20), (unsigned long long) (total_mem >> 20));
}

// `path` is UTF-8. With use_gpu == 0 nothing is offloaded (CPU fallback).
OT_API struct llama_model * ot_model_load(const char * path, int32_t use_gpu) {
    static ggml_backend_dev_t no_devices[1] = { NULL };
    struct llama_model_params params = llama_model_default_params();
    if (use_gpu) {
        params.n_gpu_layers = 999;
    } else {
        params.n_gpu_layers = 0;
        params.devices = no_devices;
    }
    return llama_model_load_from_file(path, params);
}

OT_API struct llama_context * ot_context_new(struct llama_model * model, uint32_t n_ctx, uint32_t n_batch,
                                             int32_t n_threads, int32_t swa_full, int32_t use_gpu) {
    struct llama_context_params params = llama_context_default_params();
    params.n_ctx = n_ctx;
    params.n_batch = n_batch;
    params.n_ubatch = n_batch;
    params.n_seq_max = 1;
    params.n_threads = n_threads;
    params.n_threads_batch = n_threads;
    params.swa_full = swa_full != 0;
    params.no_perf = true;
    if (!use_gpu) {
        params.offload_kqv = false;
        params.op_offload = false;
    }
    return llama_init_from_model(model, params);
}

// Decodes `n` tokens that follow the cached ones; logits for the last only.
OT_API int32_t ot_decode(struct llama_context * ctx, llama_token * tokens, int32_t n) {
    return llama_decode(ctx, llama_batch_get_one(tokens, n));
}

// Decodes `n` tokens at positions pos0... of sequence 0 with logits at every
// position (scoring candidate words).
OT_API int32_t ot_decode_all_logits(struct llama_context * ctx, const llama_token * tokens, int32_t n, int32_t pos0) {
    struct llama_batch batch = llama_batch_init(n, 0, 1);
    for (int32_t i = 0; i < n; i++) {
        batch.token[i] = tokens[i];
        batch.pos[i] = pos0 + i;
        batch.n_seq_id[i] = 1;
        batch.seq_id[i][0] = 0;
        batch.logits[i] = 1;
    }
    batch.n_tokens = n;
    int32_t rc = llama_decode(ctx, batch);
    llama_batch_free(batch);
    return rc;
}

// Index and value of the largest logit.
OT_API float ot_max_index(const float * logits, int32_t n, int32_t * index) {
    float best = -INFINITY;
    int32_t best_index = 0;
    for (int32_t i = 0; i < n; i++) {
        if (logits[i] > best) {
            best = logits[i];
            best_index = i;
        }
    }
    *index = best_index;
    return best;
}

// log(sum(exp(logits))), computed stably.
OT_API float ot_log_sum_exp(const float * logits, int32_t n) {
    float max_value = -INFINITY;
    for (int32_t i = 0; i < n; i++) {
        if (logits[i] > max_value) max_value = logits[i];
    }
    float sum = 0.0f;
    for (int32_t i = 0; i < n; i++) {
        sum += expf(logits[i] - max_value);
    }
    return max_value + logf(sum);
}
