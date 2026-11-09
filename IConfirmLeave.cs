namespace RandomDrawer
{
    public interface IConfirmLeave
    {
        /// <summary>带弹窗的确认（用于返回/关闭单页时）。返回 true 允许离开。</summary>
        bool CanLeave();

        /// <summary>无弹窗检查：是否有未保存的修改。</summary>
        bool HasUnsavedChanges();

        /// <summary>未保存修改的友好名称（用于"主页"按钮列出）。</summary>
        string UnsavedLabel { get; }
    }
}