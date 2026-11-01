using System;
using System.Drawing;

namespace Caxa3dcudeaddin
{
    /// <summary>
    /// Image → COM IPictureDisp 转换（AxHost 技法，取自官方 MyFirstAddin 样例）。
    /// CreateCommandHandler 需要此类型的按钮图标。
    /// </summary>
    internal class ConvertImage : System.Windows.Forms.AxHost
    {
        public ConvertImage() : base("")
        {
        }

        public static stdole.IPictureDisp ImageToPictureDisp(Image image)
        {
            return (stdole.IPictureDisp)GetIPictureDispFromPicture(image);
        }
    }
}
