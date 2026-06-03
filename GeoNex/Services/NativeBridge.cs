using System.Runtime.InteropServices;

namespace GeoNex.Services
{
    public static class NativeBridge
    {
        // Forçamos o EntryPoint exato para evitar confusões do compilador
        [DllImport("GeoNex.Core.dll", EntryPoint = "PontoNoPoligono", CallingConvention = CallingConvention.Cdecl)]
        public static extern int PontoNoPoligono(
            double pontoX,
            double pontoY,
            double[] coordenadas,
            int totalVertices);

        [DllImport("GeoNex.Core.dll", EntryPoint = "SimplificarDouglasPeucker", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SimplificarDouglasPeucker(
            double[] coordenadas,
            int startIndex,
            int endIndex,
            double tolerancia,
            int[] manterVertice);
    }
}