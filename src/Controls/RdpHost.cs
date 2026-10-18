using System.Windows.Forms;
using MyRemote.Services;

namespace MyRemote.Controls
{
    /// <summary>
    /// 内嵌微软 RDP 客户端控件的宿主。
    ///
    /// 关键设计一：采用 COM 后期绑定（AxHost + dynamic），不编译期引用 RDP 类型库，
    /// 这样无需 tlbimp 也能在任何环境构建，运行期加载系统自带的 mstscax.dll。
    ///
    /// 关键设计二（实机踩坑修正）：CLSID 不再写死。
    /// 原先固定使用 MsRdpClient9 的 NotSafeForScripting 变体（...6167FD），
    /// 但部分系统（如 Win10 IoT 企业版 LTSC）并未注册该变体，导致创建时抛
    /// 0x80040154（REGDB_E_CLASSNOTREG，“没有注册类”）。
    /// 现在改为通过 <see cref="RdpClientDetector"/> 在运行期自动探测本机可用的控件版本，
    /// 探测失败时回退到原 CLSID（由调用方给出明确错误提示）。
    /// </summary>
    public class RdpHost : AxHost
    {
        /// <summary>原版 CLSID（MsRdpClient9 NotSafeForScripting），作为探测失败时的回退。</summary>
        private const string LegacyClsId = "301B94BA-5D25-4A12-BFFE-3B6E7A6167FD";

        /// <summary>本机是否存在可用的 RDP 客户端控件。</summary>
        public static bool ControlAvailable => RdpClientDetector.IsAvailable;

        /// <summary>实际使用的控件 CLSID（探测结果；探测失败时为原版 CLSID）。</summary>
        public static string ResolvedClsId =>
            RdpClientDetector.IsAvailable ? RdpClientDetector.DetectedClsid : LegacyClsId;

        public RdpHost() : base(ResolvedClsId) { }

        /// <summary>返回底层 RDP COM 对象，调用方用 dynamic 访问其属性/方法。</summary>
        public dynamic Oc => GetOcx();
    }
}