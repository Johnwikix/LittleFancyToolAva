namespace FancyToolAva.Models
{
    /// <summary>
    /// 系统级窗口背景效果（仅 Windows 实际生效，其它平台回退 <see cref="None"/>）。
    /// 取值顺序即设置页下拉框顺序，持久化为索引；默认 Mica。
    /// </summary>
    public enum BackdropMode
    {
        /// <summary>云母：系统取桌面壁纸主色绘制底色（Win11 22H2+，旧系统自动回退纯色）。</summary>
        Mica = 0,

        /// <summary>系统亚克力：DWM 模糊 + 跟随主题的着色。</summary>
        Acrylic = 1,

        /// <summary>自定义亚克力：可调色调与不透明度。</summary>
        CustomAcrylic = 2,

        /// <summary>全透明：无模糊无着色，直透桌面。</summary>
        Transparent = 3,

        /// <summary>内部回退值：无效果纯色窗口（非 Windows 平台 / 不支持时使用，不在设置页展示）。</summary>
        None = 4
    }
}
