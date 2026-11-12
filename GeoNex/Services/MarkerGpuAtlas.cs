using System;
using System.Collections.Concurrent;
using SkiaSharp;

namespace GeoNex.Services
{
    /// <summary>
    /// Atlas dinâmico de texturas GPU em VRAM para renderização em lote via SKCanvas.DrawAtlas (QGIS GPU Marker Engine).
    /// </summary>
    public sealed class MarkerGpuAtlas : IDisposable
    {
        public const int DefaultAtlasSize = 2048;

        private readonly int _width;
        private readonly int _height;
        private readonly SKBitmap _atlasBitmap;
        private readonly SKCanvas _atlasCanvas;
        private readonly ConcurrentDictionary<string, SKRect> _spriteRects = new(StringComparer.Ordinal);

        private int _currentX = 2;
        private int _currentY = 2;
        private int _rowMaxHeight = 0;
        private readonly object _syncLock = new();

        public MarkerGpuAtlas(int width = DefaultAtlasSize, int height = DefaultAtlasSize)
        {
            _width = width;
            _height = height;
            _atlasBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            _atlasCanvas = new SKCanvas(_atlasBitmap);
            _atlasCanvas.Clear(SKColors.Transparent);
        }

        public bool TryGetSpriteRect(string key, out SKRect rect)
        {
            return _spriteRects.TryGetValue(key, out rect);
        }

        public SKRect GetOrAddSprite(string key, SKPicture picture, int spriteWidth, int spriteHeight)
        {
            if (_spriteRects.TryGetValue(key, out var existing))
            {
                return existing;
            }

            lock (_syncLock)
            {
                if (_spriteRects.TryGetValue(key, out existing))
                {
                    return existing;
                }

                int pad = 2;
                int allocW = spriteWidth + pad * 2;
                int allocH = spriteHeight + pad * 2;

                if (_currentX + allocW > _width)
                {
                    // Nova linha
                    _currentX = 2;
                    _currentY += _rowMaxHeight + pad;
                    _rowMaxHeight = 0;
                }

                if (_currentY + allocH > _height)
                {
                    // Atlas esgotado: limpa e reseta
                    _currentX = 2;
                    _currentY = 2;
                    _rowMaxHeight = 0;
                    _spriteRects.Clear();
                    _atlasCanvas.Clear(SKColors.Transparent);
                }

                float destX = _currentX + pad;
                float destY = _currentY + pad;

                var rect = new SKRect(destX, destY, destX + spriteWidth, destY + spriteHeight);

                int save = _atlasCanvas.Save();
                _atlasCanvas.Translate(destX, destY);

                var cull = picture.CullRect;
                float sx = cull.Width > 0 ? spriteWidth / cull.Width : 1f;
                float sy = cull.Height > 0 ? spriteHeight / cull.Height : 1f;
                _atlasCanvas.Scale(sx, sy);

                _atlasCanvas.DrawPicture(picture);
                _atlasCanvas.RestoreToCount(save);

                _currentX += allocW;
                if (allocH > _rowMaxHeight) _rowMaxHeight = allocH;

                _spriteRects.TryAdd(key, rect);
                return rect;
            }
        }

        public SKImage CreateImageSnapshot()
        {
            lock (_syncLock)
            {
                return SKImage.FromBitmap(_atlasBitmap);
            }
        }

        public void Clear()
        {
            lock (_syncLock)
            {
                _currentX = 2;
                _currentY = 2;
                _rowMaxHeight = 0;
                _spriteRects.Clear();
                _atlasCanvas.Clear(SKColors.Transparent);
            }
        }

        public void Dispose()
        {
            _atlasCanvas.Dispose();
            _atlasBitmap.Dispose();
        }
    }
}
