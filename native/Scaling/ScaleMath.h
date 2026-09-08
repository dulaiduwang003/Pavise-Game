#pragma once
#include <algorithm>
#include <cmath>
struct ScaleRect { int x, y, width, height; };
inline ScaleRect Fit(int sw, int sh, int dw, int dh) {
    if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) return {0,0,0,0};
    double s=std::min(double(dw)/sw, double(dh)/sh);
    int w=std::max(1,int(std::round(sw*s))), h=std::max(1,int(std::round(sh*s)));
    return {(dw-w)/2,(dh-h)/2,w,h};
}
inline int MapCoordinate(int p, int origin, int size, int source) {
    if (size <= 0 || source <= 0) return 0;
    return std::max(0,std::min(source-1,int((double(p-origin)*source)/size)));
}
