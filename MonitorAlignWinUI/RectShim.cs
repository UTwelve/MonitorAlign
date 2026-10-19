namespace System.Windows;

/// <summary>
/// WinUI 3 工程专用垫片：共享的 Core/DisplayInfo.cs 里用了 <c>System.Windows.Rect</c>
/// （WPF 版的历史类型）。WinUI 3 工程不引用 WPF，这里按需补一个同命名空间的最小结构，
/// 让共享源码零改动地编译。
///
/// ⚠ 语义必须与 WPF 对齐：四参构造是 <c>(x, y, width, height)</c>，**不是** (left, top, right, bottom)。
/// 第一版垫片按"两条边"实现，于是所有左/上坐标非 0 的屏算出来的 Width/Height 都是 0 ——
/// 只在副屏位于正方向时暴露，被 <c>--geomcheck</c> 的 6 项断言当场抓住。
/// 以后凡是往这里加成员，先对照 WPF 的 XML 文档确认参数含义。
///
/// 只实现 DisplayInfo 实际用到的成员（四边界 + 宽高 + ctor）。WPF 版不受影响。
/// </summary>
public struct Rect
{
    public Rect(double x, double y, double width, double height)
    {
        Left = x;
        Top = y;
        Width = width;
        Height = height;
    }

    public double Left { get; }
    public double Top { get; }
    public double Width { get; }
    public double Height { get; }

    public double Right => Left + Width;
    public double Bottom => Top + Height;
}
