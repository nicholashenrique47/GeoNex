#pragma once

namespace geonex {
    using ParsePointsFunction = void (*)(const unsigned char*, float*, int, double, double) noexcept;

    void ParsePointsScalar(const unsigned char* input, float* output, int count,
        double offsetX, double offsetY) noexcept;
    void ParsePointsAvx2(const unsigned char* input, float* output, int count,
        double offsetX, double offsetY) noexcept;

    // Selected once, including GEONEX_FORCE_SCALAR=1. Change the environment
    // before starting the process, not between frames.
    ParsePointsFunction GetPointParser() noexcept;
}
