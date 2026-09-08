#include "NativeCpu.h"
#include <cstddef>
#include <immintrin.h>

namespace geonex {
    // This translation unit alone is compiled with /arch:AVX2. Never enable
    // whole-program optimization across this boundary: the baseline must remain
    // executable on x64 processors without AVX and on OSes without YMM support.
    void ParsePointsAvx2(const unsigned char* input, float* output, int count,
        double offsetX, double offsetY) noexcept {
        if (!input || !output || count <= 0) return;
        const __m256d offsets = _mm256_setr_pd(offsetX, offsetY, offsetX, offsetY);
        const __m256d flipY = _mm256_setr_pd(1.0, -1.0, 1.0, -1.0);
        const auto pointCount = static_cast<std::size_t>(count);
        std::size_t i = 0;
        for (; i + 1 < pointCount; i += 2) {
            const __m256d xy = _mm256_loadu_pd(reinterpret_cast<const double*>(input + i * 16));
            const __m256d local = _mm256_mul_pd(_mm256_sub_pd(xy, offsets), flipY);
            _mm_storeu_ps(output + i * 2, _mm256_cvtpd_ps(local));
        }
        if (i < pointCount)
            ParsePointsScalar(input + i * 16, output + i * 2, 1, offsetX, offsetY);
    }
}
