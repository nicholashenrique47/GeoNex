#include "NativeCpu.h"
#include <cstddef>
#include <cstring>
#include <intrin.h>
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>

namespace {
    bool SupportsAvx2() noexcept {
        int cpu[4]{};
        __cpuid(cpu, 0);
        if (cpu[0] < 7) return false;
        __cpuidex(cpu, 1, 0);
        constexpr int required = (1 << 26) | (1 << 27) | (1 << 28); // XSAVE, OSXSAVE, AVX
        if ((cpu[2] & required) != required) return false;
        // XGETBV is only legal after checking OSXSAVE. Both XMM and YMM state
        // must be enabled by the OS before any code in ParseAvx2.cpp executes.
        if ((_xgetbv(0) & 0x6) != 0x6) return false;
        __cpuidex(cpu, 7, 0);
        return (cpu[1] & (1 << 5)) != 0;
    }

    bool ForceScalar() noexcept {
        char value[8]{};
        const DWORD length = GetEnvironmentVariableA("GEONEX_FORCE_SCALAR", value, sizeof(value));
        return length == 1 && value[0] == '1';
    }

    struct ParserSelection {
        bool avx2Available;
        int backend; // 0 = scalar/SSE2 baseline; 1 = AVX2
        geonex::ParsePointsFunction parser;
    };

    const ParserSelection& Selection() noexcept {
        static const ParserSelection selection = []() noexcept {
            const bool available = SupportsAvx2();
            const bool useAvx2 = available && !ForceScalar();
            return ParserSelection{available, useAvx2 ? 1 : 0,
                useAvx2 ? geonex::ParsePointsAvx2 : geonex::ParsePointsScalar};
        }();
        return selection;
    }
}

namespace geonex {
    void ParsePointsScalar(const unsigned char* input, float* output, int count,
        double offsetX, double offsetY) noexcept {
        if (!input || !output || count <= 0) return;
        const auto pointCount = static_cast<std::size_t>(count);
        #pragma loop(no_vector)
        for (std::size_t i = 0; i < pointCount; ++i) {
            double xy[2];
            // SHP records are not necessarily aligned to double boundaries.
            std::memcpy(xy, input + i * sizeof(xy), sizeof(xy));
            output[i * 2] = static_cast<float>(xy[0] - offsetX);
            output[i * 2 + 1] = -static_cast<float>(xy[1] - offsetY);
        }
    }

    ParsePointsFunction GetPointParser() noexcept { return Selection().parser; }
}

extern "C" {
    __declspec(dllexport) int GetNativePointParserBackend() noexcept { return Selection().backend; }
    __declspec(dllexport) int IsNativeAvx2Available() noexcept { return Selection().avx2Available ? 1 : 0; }

    // Reference entry point for differential validation and diagnostic benchmarks.
    __declspec(dllexport) void ParseShapefilePartScalar(const unsigned char* input, float* output,
        int count, double offsetX, double offsetY) noexcept {
        geonex::ParsePointsScalar(input, output, count, offsetX, offsetY);
    }
}
