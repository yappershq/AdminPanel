using SqlSugar;

namespace AdminPanel.Database;

/// <summary>Row in adminpanel_reasons. action_type matches the ActionType enum string value.</summary>
[SugarTable("adminpanel_reasons")]
internal sealed class ReasonPreset
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnName = "id")]
    public int Id { get; set; }

    [SugarColumn(ColumnName = "action_type", Length = 32)]
    public string ActionType { get; set; } = "";

    [SugarColumn(ColumnName = "label", Length = 80)]
    public string Label { get; set; } = "";

    [SugarColumn(ColumnName = "reason_text", Length = 200)]
    public string ReasonText { get; set; } = "";

    [SugarColumn(ColumnName = "sort_order")]
    public short SortOrder { get; set; } = 0;

    [SugarColumn(ColumnName = "enabled")]
    public byte Enabled { get; set; } = 1;

    [SugarColumn(ColumnName = "server_tag", Length = 32)]
    public string ServerTag { get; set; } = "all";
}

/// <summary>Row in adminpanel_meta — version bump triggers hot-reload.</summary>
[SugarTable("adminpanel_meta")]
internal sealed class MetaRow
{
    [SugarColumn(IsPrimaryKey = true, ColumnName = "key_name", Length = 32)]
    public string KeyName { get; set; } = "";

    [SugarColumn(ColumnName = "value")]
    public long Value { get; set; } = 0;
}
