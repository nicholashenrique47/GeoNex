#include <cmath>

extern "C" {

    // ====================================================================
    // FUNÇÃO 1: BUSCA ESPACIAL - RAY CASTING (Ponto no Polígono)
    // ====================================================================
    __declspec(dllexport) int PontoNoPoligono(double pontoX, double pontoY, const double* coordenadas, int totalVertices) {
        int dentro = 0;
        // O algoritmo lança um raio imaginário a partir do ponto e conta quantas vezes cruza as linhas do polígono
        for (int i = 0, j = totalVertices - 1; i < totalVertices; j = i++) {
            double xi = coordenadas[i * 2];
            double yi = coordenadas[i * 2 + 1];
            double xj = coordenadas[j * 2];
            double yj = coordenadas[j * 2 + 1];

            if (((yi > pontoY) != (yj > pontoY)) &&
                (pontoX < (xj - xi) * (pontoY - yi) / (yj - yi) + xi)) {
                dentro = !dentro;
            }
        }
        return dentro; // Retorna 1 (Sim) ou 0 (Não)
    }

    // ====================================================================
    // 
    // FUNÇÕES AUXILIARES PARA O DOUGLAS-PEUCKER
    // ====================================================================
    double DistanciaPerpendicular(double px, double py, double l1x, double l1y, double l2x, double l2y) {
        double num = std::abs((l2y - l1y) * px - (l2x - l1x) * py + l2x * l1y - l2y * l1x);
        double den = std::sqrt(std::pow(l2y - l1y, 2) + std::pow(l2x - l1x, 2));
        return den == 0 ? 0 : num / den;
    }

    // ====================================================================
    // FUNÇÃO 2: SIMPLIFICAÇÃO - DOUGLAS-PEUCKER
    // ====================================================================
    __declspec(dllexport) void SimplificarDouglasPeucker(const double* coordenadas, int startIndex, int endIndex, double tolerancia, int* manterVertice) {
        double maxDistancia = 0.0;
        int indexMaiorDistancia = startIndex;

        for (int i = startIndex + 1; i < endIndex; i++) {
            double dist = DistanciaPerpendicular(
                coordenadas[i * 2], coordenadas[i * 2 + 1],
                coordenadas[startIndex * 2], coordenadas[startIndex * 2 + 1],
                coordenadas[endIndex * 2], coordenadas[endIndex * 2 + 1]
            );

            if (dist > maxDistancia) {
                maxDistancia = dist;
                indexMaiorDistancia = i;
            }
        }

        // Se a distância for maior que a tolerância, este vértice é importante para manter o formato do lote
        if (maxDistancia > tolerancia) {
            manterVertice[indexMaiorDistancia] = 1;

            // Recursão matemática para dividir e conquistar as metades restantes
            SimplificarDouglasPeucker(coordenadas, startIndex, indexMaiorDistancia, tolerancia, manterVertice);
            SimplificarDouglasPeucker(coordenadas, indexMaiorDistancia, endIndex, tolerancia, manterVertice);
        }
    }
}
