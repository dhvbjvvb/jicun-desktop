using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Jicun.Desktop.Converters;

/// <summary>
/// 字符串 URL 直接喂给 Image.Source（x:Bind 不做隐式转换，所以要过一道手）。
/// ConverterParameter 给一个像素宽度 = 解码尺寸上限，缩略图别按原图整张解。
/// </summary>
public sealed class UrlToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var bitmap = new BitmapImage();

            // 缩略图格子只有一两百像素宽，源图却常常上千像素：不限定解码尺寸就是按原图
            // 整张解进内存（一张 3000x2000 ≈ 24MB，一帖几十张就是几百 MB 常驻）。
            // 必须**在开始解码之前**设好，所以这里不能走 new BitmapImage(uri) 那个构造
            // 函数 —— 它一构造就开始按原尺寸解码了。
            // ponytail: 只限宽、不限高。超长图（平台上真有 1080x20000 的长图）会按 456 宽解成
            // 456x8444 ≈ 15MB，仍比格子大两个数量级 —— 真要「塞进格子」得换 BitmapDecoder +
            // BitmapTransform（能同时限宽高），代价是每张图都变成异步解码。现在这样够用。
            if (parameter is not null && int.TryParse(parameter.ToString(), out var width) && width > 0)
                bitmap.DecodePixelWidth = width;

            bitmap.UriSource = new Uri(url);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
