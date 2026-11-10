namespace RandomDrawer
{
    /// <summary>
    /// 页面需要优先处理 ESC 键（如关闭内部弹层）时实现此接口。
    /// 返回 true 表示已处理，MainWindow 不再做其它处理。
    /// </summary>
    public interface IHandleEscape
    {
        bool TryHandleEscape();
    }
}