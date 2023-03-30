using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using ImGuiNET;
using OtterGui;
using OtterGui.Classes;
using OtterGui.Raii;
using OtterGui.Table;
using Penumbra.GameData.Enums;
using Penumbra.String;

namespace Penumbra.UI;

internal sealed class ResourceWatcherTable : Table<Record>
{
    public ResourceWatcherTable(Configuration config, ICollection<Record> records)
        : base("##records",
            records,
            new PathColumn { Label               = "Path" },
            new RecordTypeColumn(config) { Label = "Record" },
            new CollectionColumn { Label         = "Collection" },
            new ObjectColumn { Label             = "Game Object" },
            new CustomLoadColumn { Label         = "Custom" },
            new SynchronousLoadColumn { Label    = "Sync" },
            new OriginalPathColumn { Label       = "Original Path" },
            new ResourceCategoryColumn { Label   = "Category" },
            new ResourceTypeColumn { Label       = "Type" },
            new HandleColumn { Label             = "Resource" },
            new RefCountColumn { Label           = "#Ref" },
            new DateColumn { Label               = "Time" }
        )
    { }

    public void Reset()
        => FilterDirty = true;

    private sealed class PathColumn : ColumnString<Record>
    {
        public override float Width
            => 300 * UiHelpers.Scale;

        public override string ToName(Record item)
            => item.Path.ToString();

        public override int Compare(Record lhs, Record rhs)
            => lhs.Path.CompareTo(rhs.Path);

        public override void DrawColumn(Record item, int _)
            => DrawByteString(item.Path, 280 * UiHelpers.Scale);
    }

    private static unsafe void DrawByteString(ByteString path, float length)
    {
        Vector2 vec;
        ImGuiNative.igCalcTextSize(&vec, path.Path, path.Path + path.Length, 0, 0);
        if (vec.X <= length)
        {
            ImGuiNative.igTextUnformatted(path.Path, path.Path + path.Length);
        }
        else
        {
            var        fileName = path.LastIndexOf((byte)'/');
            ByteString shortPath;
            if (fileName != -1)
            {
                using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(2 * UiHelpers.Scale));
                using var font  = ImRaii.PushFont(UiBuilder.IconFont);
                ImGui.TextUnformatted(FontAwesomeIcon.EllipsisH.ToIconString());
                ImGui.SameLine();
                shortPath = path.Substring(fileName, path.Length - fileName);
            }
            else
            {
                shortPath = path;
            }

            ImGuiNative.igTextUnformatted(shortPath.Path, shortPath.Path + shortPath.Length);
            if (ImGui.IsItemClicked())
                ImGuiNative.igSetClipboardText(path.Path);

            if (ImGui.IsItemHovered())
                ImGuiNative.igSetTooltip(path.Path);
        }
    }

    private sealed class RecordTypeColumn : ColumnFlags<RecordType, Record>
    {
        private readonly Configuration _config;

        public RecordTypeColumn(Configuration config)
        {
            AllFlags = ResourceWatcher.AllRecords;
            _config  = config;
        }

        public override float Width
            => 80 * UiHelpers.Scale;

        public override bool FilterFunc(Record item)
            => FilterValue.HasFlag(item.RecordType);

        public override RecordType FilterValue
            => _config.ResourceWatcherRecordTypes;

        protected override void SetValue(RecordType value, bool enable)
        {
            if (enable)
                _config.ResourceWatcherRecordTypes |= value;
            else
                _config.ResourceWatcherRecordTypes &= ~value;

            Penumbra.Config.Save();
        }

        public override void DrawColumn(Record item, int idx)
        {
            ImGui.TextUnformatted(item.RecordType switch
            {
                RecordType.Request      => "REQ",
                RecordType.ResourceLoad => "LOAD",
                RecordType.FileLoad     => "FILE",
                RecordType.Destruction  => "DEST",
                _                       => string.Empty,
            });
        }
    }

    private sealed class DateColumn : Column<Record>
    {
        public override float Width
            => 80 * UiHelpers.Scale;

        public override int Compare(Record lhs, Record rhs)
            => lhs.Time.CompareTo(rhs.Time);

        public override void DrawColumn(Record item, int _)
            => ImGui.TextUnformatted($"{item.Time.ToLongTimeString()}.{item.Time.Millisecond:D4}");
    }


    private sealed class CollectionColumn : ColumnString<Record>
    {
        public override float Width
            => 80 * UiHelpers.Scale;

        public override string ToName(Record item)
            => item.Collection?.Name ?? string.Empty;
    }

    private sealed class ObjectColumn : ColumnString<Record>
    {
        public override float Width
            => 200 * UiHelpers.Scale;

        public override string ToName(Record item)
            => item.AssociatedGameObject;
    }

    private sealed class OriginalPathColumn : ColumnString<Record>
    {
        public override float Width
            => 200 * UiHelpers.Scale;

        public override string ToName(Record item)
            => item.OriginalPath.ToString();

        public override int Compare(Record lhs, Record rhs)
            => lhs.OriginalPath.CompareTo(rhs.OriginalPath);

        public override void DrawColumn(Record item, int _)
            => DrawByteString(item.OriginalPath, 190 * UiHelpers.Scale);
    }

    private sealed class ResourceCategoryColumn : ColumnFlags<ResourceCategoryFlag, Record>
    {
        public ResourceCategoryColumn()
            => AllFlags = ResourceExtensions.AllResourceCategories;

        public override float Width
            => 80 * UiHelpers.Scale;

        public override bool FilterFunc(Record item)
            => FilterValue.HasFlag(item.Category);

        public override ResourceCategoryFlag FilterValue
            => Penumbra.Config.ResourceWatcherResourceCategories;

        protected override void SetValue(ResourceCategoryFlag value, bool enable)
        {
            if (enable)
                Penumbra.Config.ResourceWatcherResourceCategories |= value;
            else
                Penumbra.Config.ResourceWatcherResourceCategories &= ~value;

            Penumbra.Config.Save();
        }

        public override void DrawColumn(Record item, int idx)
        {
            ImGui.TextUnformatted(item.Category.ToString());
        }
    }

    private sealed class ResourceTypeColumn : ColumnFlags<ResourceTypeFlag, Record>
    {
        public ResourceTypeColumn()
        {
            AllFlags = Enum.GetValues<ResourceTypeFlag>().Aggregate((v, f) => v | f);
            for (var i = 0; i < Names.Length; ++i)
                Names[i] = Names[i].ToLowerInvariant();
        }

        public override float Width
            => 50 * UiHelpers.Scale;

        public override bool FilterFunc(Record item)
            => FilterValue.HasFlag(item.ResourceType);

        public override ResourceTypeFlag FilterValue
            => Penumbra.Config.ResourceWatcherResourceTypes;

        protected override void SetValue(ResourceTypeFlag value, bool enable)
        {
            if (enable)
                Penumbra.Config.ResourceWatcherResourceTypes |= value;
            else
                Penumbra.Config.ResourceWatcherResourceTypes &= ~value;

            Penumbra.Config.Save();
        }

        public override void DrawColumn(Record item, int idx)
        {
            ImGui.TextUnformatted(item.ResourceType.ToString().ToLowerInvariant());
        }
    }

    private sealed class HandleColumn : ColumnString<Record>
    {
        public override float Width
            => 120 * UiHelpers.Scale;

        public override unsafe string ToName(Record item)
            => item.Handle == null ? string.Empty : $"0x{(ulong)item.Handle:X}";

        public override unsafe void DrawColumn(Record item, int _)
        {
            using var font = ImRaii.PushFont(UiBuilder.MonoFont, item.Handle != null);
            ImGuiUtil.RightAlign(ToName(item));
        }
    }

    [Flags]
    private enum BoolEnum : byte
    {
        True    = 0x01,
        False   = 0x02,
        Unknown = 0x04,
    }

    private class OptBoolColumn : ColumnFlags<BoolEnum, Record>
    {
        private BoolEnum _filter;

        public OptBoolColumn()
        {
            AllFlags =  BoolEnum.True | BoolEnum.False | BoolEnum.Unknown;
            _filter  =  AllFlags;
            Flags    &= ~ImGuiTableColumnFlags.NoSort;
        }

        protected bool FilterFunc(OptionalBool b)
            => b.Value switch
            {
                null  => _filter.HasFlag(BoolEnum.Unknown),
                true  => _filter.HasFlag(BoolEnum.True),
                false => _filter.HasFlag(BoolEnum.False),
            };

        public override BoolEnum FilterValue
            => _filter;

        protected override void SetValue(BoolEnum value, bool enable)
        {
            if (enable)
                _filter |= value;
            else
                _filter &= ~value;
        }

        protected static void DrawColumn(OptionalBool b)
        {
            using var font = ImRaii.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(b.Value switch
            {
                null  => string.Empty,
                true  => FontAwesomeIcon.Check.ToIconString(),
                false => FontAwesomeIcon.Times.ToIconString(),
            });
        }
    }

    private sealed class CustomLoadColumn : OptBoolColumn
    {
        public override float Width
            => 60 * UiHelpers.Scale;

        public override bool FilterFunc(Record item)
            => FilterFunc(item.CustomLoad);

        public override void DrawColumn(Record item, int idx)
            => DrawColumn(item.CustomLoad);
    }

    private sealed class SynchronousLoadColumn : OptBoolColumn
    {
        public override float Width
            => 45 * UiHelpers.Scale;

        public override bool FilterFunc(Record item)
            => FilterFunc(item.Synchronously);

        public override void DrawColumn(Record item, int idx)
            => DrawColumn(item.Synchronously);
    }

    private sealed class RefCountColumn : Column<Record>
    {
        public override float Width
            => 30 * UiHelpers.Scale;

        public override void DrawColumn(Record item, int _)
            => ImGuiUtil.RightAlign(item.RefCount.ToString());

        public override int Compare(Record lhs, Record rhs)
            => lhs.RefCount.CompareTo(rhs.RefCount);
    }
}
