//! \file       ImageDWQ.cs
//! \date       Sat Aug 01 13:18:46 2015
//! \brief      Black Cyc image format.
//
// Copyright (C) 2015 by morkt
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameRes.Utility;

namespace GameRes.Formats.BlackCyc
{
    internal class ResourceHeader
    {
        public static readonly Regex PackTypeRe = new Regex (@"^PACKTYPE=(\d+)(A?) +$");

        public byte[] Bytes { get; private set; }
        public int PackType { get; private set; }
        public bool   AType { get; private set; }

        public static ResourceHeader Read (Stream file)
        {
            var header = new ResourceHeader { Bytes = new byte[0x40] };
            if (0x40 != file.Read (header.Bytes, 0, 0x40))
                return null;

            var header_string = Encoding.ASCII.GetString (header.Bytes, 0x30, 0x10);
            var match = PackTypeRe.Match (header_string);
            if (!match.Success)
                return null;
            header.PackType = ushort.Parse (match.Groups[1].Value);
            header.AType = match.Groups[2].Value.Length > 0;
            return header;
        }
    }

    internal class DwqMetaData : ImageMetaData
    {
        public string BaseType;
        public int    PackedSize;
        public int    PackType;
        public bool   AType;
    }

    [Export(typeof(ImageFormat))]
    public class DwqFormat : ImageFormat
    {
        public override string         Tag { get { return "DWQ"; } }
        public override string Description { get { return "Black Cyc image format"; } }
        public override uint     Signature { get { return 0; } }

        public DwqFormat ()
        {
            Signatures = new uint[] { 0x4745504A, 0x20504D42, 0x20474E50, 0x4B434150 };
        }

        static ImageFormat GetFormat (string tag)
        {
            return FormatCatalog.Instance.ImageFormats.FirstOrDefault (x => x.Tag == tag);
        }

        static readonly Lazy<ImageFormat> JpegFormat = new Lazy<ImageFormat> (() => GetFormat ("JPEG"));
        static readonly Lazy<ImageFormat> PngFormat  = new Lazy<ImageFormat> (() => GetFormat ("PNG"));

        public override ImageMetaData ReadMetaData (Stream stream)
        {
            var header = ResourceHeader.Read (stream);
            if (null == header)
                return null;
            return new DwqMetaData
            {
                Width  = LittleEndian.ToUInt32 (header.Bytes, 0x24),
                Height = LittleEndian.ToUInt32 (header.Bytes, 0x28),
                BPP = 32,
                BaseType = Encoding.ASCII.GetString (header.Bytes, 0, 0x10).TrimEnd(),
                PackedSize = LittleEndian.ToInt32 (header.Bytes, 0x20),
                PackType = header.PackType,
                AType = header.AType,
            };
        }

        public override ImageData Read (Stream stream, ImageMetaData info)
        {
            var meta = info as DwqMetaData;
            if (null == meta)
                throw new ArgumentException ("DwqFormat.Read should be supplied with DwqMetaData", "info");

            BitmapSource bitmap = null;
            switch (meta.PackType)
            {
            case 5: // JPEG
                using (var jpeg = new StreamRegion (stream, 0x40, stream.Length-0x40, true))
                    return JpegFormat.Value.Read (jpeg, info);

            case 8: // PNG
                using (var png = new StreamRegion (stream, 0x40, stream.Length-0x40, true))
                    return PngFormat.Value.Read (png, info);

            case 0: // BMP
                using (var bmp = new StreamRegion (stream, 0x40, stream.Length-0x40, true))
                {
                    var decoder = new BmpBitmapDecoder (bmp, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    // non-conforming BMP, flip image vertically
                    bitmap = new TransformedBitmap (decoder.Frames[0], new ScaleTransform { ScaleY = -1 });
                    return new ImageData (bitmap, info);
                }

            case 7: // JPEG+MASK
                using (var jpeg = new StreamRegion (stream, 0x40, meta.PackedSize, true))
                {
                    var decoder = new JpegBitmapDecoder (jpeg, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    bitmap = decoder.Frames[0];
                }
                break;

            case 3: // PACKBMP+MASK
                using (var bmp = new StreamRegion (stream, 0x40, meta.PackedSize, true))
                {
                    var reader = new DwqBmpReader (bmp, meta);
                    reader.Unpack();
                    bitmap = BitmapSource.Create ((int)info.Width, (int)info.Height,
                                ImageData.DefaultDpiX, ImageData.DefaultDpiY,
                                reader.Format, reader.Palette, reader.Data, reader.Stride);
                }
                break;
            }
            if (null == bitmap)
                throw new NotImplementedException();
            if (meta.AType)
            {
                int mask_offset = 0x40+meta.PackedSize;
                using (var mask = new StreamRegion (stream, mask_offset, stream.Length-mask_offset, true))
                {
                    var reader = new DwqBmpReader (mask, meta);
                    if (8 == reader.Format.BitsPerPixel) // mask should be represented as 8bpp bitmap
                    {
                        reader.Unpack();
                        var alpha = reader.Data;
                        var palette = reader.Palette.Colors;
                        for (int i = 0; i < alpha.Length; ++i)
                        {
                            var color = palette[alpha[i]];
                            int A = (color.R + color.G + color.B) / 3;
                            alpha[i] = (byte)A;
                        }
                        bitmap = ApplyAlphaChannel (bitmap, reader.Data);
                    }
                }
            }
            bitmap.Freeze();
            return new ImageData (bitmap, meta);
        }

        public override void Write (Stream file, ImageData image)
        {
            throw new NotImplementedException ("DwqFormat.Write not implemented");
        }

        private BitmapSource ApplyAlphaChannel (BitmapSource bitmap, byte[] alpha)
        {
            if (bitmap.Format.BitsPerPixel != 32)
                bitmap = new FormatConvertedBitmap (bitmap, PixelFormats.Bgr32, null, 0);

            int stride = bitmap.PixelWidth * 4;
            byte[] pixels = new byte[stride * bitmap.PixelHeight];
            int asrc = 0;
            bitmap.CopyPixels (pixels, stride, 0);
            for (int dst = 3; dst < pixels.Length; dst += 4)
            {
                pixels[dst] = alpha[asrc++];
            }
            return BitmapSource.Create (bitmap.PixelWidth, bitmap.PixelHeight,
                        ImageData.DefaultDpiX, ImageData.DefaultDpiY,
                        PixelFormats.Bgra32, null, pixels, stride);
        }
    }

    internal class DwqBmpReader
    {
        Stream      m_input;
        byte[]      m_pixels;
        int         m_width;
        int         m_height;

        public byte[]           Data { get { return m_pixels; } }
        public int            Stride { get; private set; }
        public PixelFormat    Format { get; private set; }
        public BitmapPalette Palette { get; private set; }

        public DwqBmpReader (Stream input, DwqMetaData info)
        {
            m_input = input;
            m_width = (int)info.Width;
            m_height = (int)info.Height;
            var header = new byte[0x36];
            if (header.Length != m_input.Read (header, 0, header.Length))
                throw new InvalidFormatException();
            int w = LittleEndian.ToInt32 (header, 0x12);
            int h = LittleEndian.ToInt32 (header, 0x16);
            if (w != m_width || h != m_height)
                throw new InvalidFormatException();

            int bpp = LittleEndian.ToUInt16 (header, 0x1C);
            switch (bpp)
            {
            case 8:     Format = PixelFormats.Indexed8; Stride = m_width; break;
            case 16:    Format = PixelFormats.Bgr565;   Stride = m_width*2; break;
            case 24:    Format = PixelFormats.Bgr24;    Stride = m_width*3; break;
            case 32:    Format = PixelFormats.Bgr32;    Stride = m_width*4; break;
            default:    throw new InvalidFormatException();
            }
            if (8 == bpp)
            {
                int colors = Math.Min (LittleEndian.ToInt32 (header, 0x2E), 0x100);
                ReadPalette (colors);
            }
            uint data_position = LittleEndian.ToUInt32 (header, 0xA);
            m_input.Position = data_position;
            m_pixels = new byte[Stride*m_height];
        }

        private void ReadPalette (int colors)
        {
            int palette_size = colors * 4;
            var palette_data = new byte[palette_size];
            if (palette_size != m_input.Read (palette_data, 0, palette_size))
                throw new InvalidFormatException();
            var palette = new Color[colors];
            for (int i = 0; i < palette.Length; ++i)
            {
                byte r = palette_data[i*4];
                byte g = palette_data[i*4+1];
                byte b = palette_data[i*4+2];
                palette[i] = Color.FromRgb (r, g, b);
            }
            Palette = new BitmapPalette (palette);
        }

        public void Unpack () // sub_408990
        {
            var prev_line = new byte[Stride];
            int dst = 0;
            for (int y = 0; y < m_height; ++y)
            {
                for (int x = 0; x < Stride; )
                {
                    int b = m_input.ReadByte();
                    if (0 != b)
                    {
                        if (-1 == b)
                            throw new EndOfStreamException();
                        m_pixels[dst + x++] = (byte)b;
                    }
                    else
                    {
                        int count = m_input.ReadByte();
                        if (-1 == count)
                            throw new EndOfStreamException();
                        for (int i = 0; i < count; ++i)
                            m_pixels[dst + x++] = 0;
                    }
                }
                for (int i = 0; i < Stride; ++i)
                {
                    m_pixels[dst] ^= prev_line[i];
                    prev_line[i] = m_pixels[dst++];
                }
            }
        }
    }
}
