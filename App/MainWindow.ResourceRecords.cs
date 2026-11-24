using RpfPreview.Core;
using System.Windows;
using System.Windows.Controls;

namespace RpfPreview.App;

public partial class MainWindow
{
    private byte[]? _recordData;
    private int _recordNumber;
    private bool _settingRecords;
    private sealed record BlockChoice(int Number, string Label);
    private sealed record FieldTarget(uint Hash, RageMetaArray Array)
    {
        public string Label => $"0x{Hash:X8} → block {Array.TargetBlock}";
    }

    private void ResetRecords()
    {
        _recordData = null;
        if (ResourceRecords == null) return;
        ResourceRecords.Visibility = Visibility.Collapsed;
        ResourceBlocks.ItemsSource = ArrayTargets.ItemsSource = null;
    }

    private void LoadRecords(byte[] bytes, ArchiveEntry entry)
    {
        if (entry.ResourceSystemBytes == null && bytes.LongLength != entry.Size) return;
        int length = entry.ResourceSystemBytes ?? bytes.Length;
        if (length > bytes.Length || RageResourceMetadata.Inspect(bytes.AsSpan(0, length)) is not { } directory) return;
        _recordData = length == bytes.Length ? bytes : bytes[..length];
        _recordNumber = 1;
        _settingRecords = true;
        try
        {
            ResourceBlocks.ItemsSource = directory.Blocks.Select((b, i) => new BlockChoice(i + 1,
                $"Block {i + 1} · 0x{b.Type:X8}{(i + 1 == directory.RootBlock ? " · root" : "")}")).ToArray();
            ResourceBlocks.SelectedIndex = directory.RootBlock - 1;
            ResourceRecords.Visibility = Visibility.Visible;
        }
        finally { _settingRecords = false; }
        ShowRecord(true);
    }

    private void ShowRecord(bool keepOverview = false)
    {
        if (_recordData == null || ResourceBlocks.SelectedItem is not BlockChoice choice) return;
        var item = RageResourceMetadata.InspectRecord(_recordData, choice.Number, _recordNumber);
        PreviousRecord.IsEnabled = item != null && _recordNumber > 1;
        NextRecord.IsEnabled = item?.RecordCount > _recordNumber;
        RecordLabel.Text = item?.RecordCount is int count ? $"{_recordNumber:N0} / {count:N0}" : "";
        var targets = item?.Fields.Where(f => f.Array?.TargetBlock > 0).Select(f => new FieldTarget(f.NameHash, f.Array!)).ToArray() ?? [];
        ArrayTargets.ItemsSource = targets; ArrayTargets.SelectedIndex = targets.Length > 0 ? 0 : -1;
        FollowRecord.Visibility = ArrayTargets.Visibility = targets.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!keepOverview) TextPreview.Text = item == null ? "This record has no readable structure." : RageResourceMetadata.DescribeRecord(item);
    }

    private void ResourceBlock_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingRecords) return;
        _recordNumber = 1; ShowRecord();
    }
    private void PreviousRecord_Click(object sender, RoutedEventArgs e) { _recordNumber--; ShowRecord(); }
    private void NextRecord_Click(object sender, RoutedEventArgs e) { _recordNumber++; ShowRecord(); }
    private void FollowRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_recordData == null || ArrayTargets.SelectedItem is not FieldTarget field ||
            RageResourceMetadata.InspectRecord(_recordData, field.Array.TargetBlock) is not { } target) return;
        _settingRecords = true;
        try { ResourceBlocks.SelectedIndex = field.Array.TargetBlock - 1; }
        finally { _settingRecords = false; }
        _recordNumber = target.RecordCount is int count && target.Length > 0 ? Math.Min(count, 1 + field.Array.TargetOffset / target.Length) : 1;
        ShowRecord();
    }
}
