namespace RetainerRepricer;

public unsafe sealed partial class Plugin
{
    internal bool CanEditAutoListBlocklists => !IsRunning && !_autoRetainerHandoffActive;

    internal void OpenAutoListBlocklists(ulong cid) => ConfigWindow.OpenBlocklistsForCharacter(cid);

    internal void OpenAutoListBlocklistItem(ulong cid, string listId, uint itemId)
        => ConfigWindow.OpenBlocklistsForItem(cid, listId, itemId);
}
