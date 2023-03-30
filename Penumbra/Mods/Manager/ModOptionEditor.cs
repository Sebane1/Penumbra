using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface.Internal.Notifications;
using OtterGui;
using OtterGui.Filesystem;
using Penumbra.Api.Enums;
using Penumbra.Meta.Manipulations;
using Penumbra.Services;
using Penumbra.String.Classes;
using Penumbra.Util;

namespace Penumbra.Mods;

public class ModOptionEditor
{
    private readonly CommunicatorService _communicator;
    private readonly SaveService         _saveService;

    public ModOptionEditor(CommunicatorService communicator, SaveService saveService)
    {
        _communicator = communicator;
        _saveService  = saveService;
    }

    /// <summary> Change the type of a group given by mod and index to type, if possible. </summary>
    public void ChangeModGroupType(Mod mod, int groupIdx, GroupType type)
    {
        var group = mod._groups[groupIdx];
        if (group.Type == type)
            return;

        mod._groups[groupIdx] = group.Convert(type);
        _saveService.QueueSave(new ModSaveGroup(mod, groupIdx));
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.GroupTypeChanged, mod, groupIdx, -1, -1);
    }

    /// <summary> Change the settings stored as default options in a mod.</summary>
    public void ChangeModGroupDefaultOption(Mod mod, int groupIdx, uint defaultOption)
    {
        var group = mod._groups[groupIdx];
        if (group.DefaultSettings == defaultOption)
            return;

        group.DefaultSettings = defaultOption;
        _saveService.QueueSave(new ModSaveGroup(mod, groupIdx));
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.DefaultOptionChanged, mod, groupIdx, -1, -1);
    }

    /// <summary> Rename an option group if possible. </summary>
    public void RenameModGroup(Mod mod, int groupIdx, string newName)
    {
        var group   = mod._groups[groupIdx];
        var oldName = group.Name;
        if (oldName == newName || !VerifyFileName(mod, group, newName, true))
            return;

        _saveService.ImmediateDelete(new ModSaveGroup(mod, groupIdx));
        var _ = group switch
        {
            SingleModGroup s => s.Name = newName,
            MultiModGroup m  => m.Name = newName,
            _                => newName,
        };

        _saveService.ImmediateSave(new ModSaveGroup(mod, groupIdx));
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.GroupRenamed, mod, groupIdx, -1, -1);
    }

    /// <summary> Add a new mod, empty option group of the given type and name. </summary>
    public void AddModGroup(Mod mod, GroupType type, string newName)
    {
        if (!VerifyFileName(mod, null, newName, true))
            return;

        var maxPriority = mod._groups.Count == 0 ? 0 : mod._groups.Max(o => o.Priority) + 1;

        mod._groups.Add(type == GroupType.Multi
            ? new MultiModGroup
            {
                Name     = newName,
                Priority = maxPriority,
            }
            : new SingleModGroup
            {
                Name     = newName,
                Priority = maxPriority,
            });
        _saveService.ImmediateSave(new ModSaveGroup(mod, mod._groups.Count - 1));
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.GroupAdded, mod, mod._groups.Count - 1, -1, -1);
    }

    /// <summary> Delete a given option group. Fires an event to prepare before actually deleting. </summary>
    public void DeleteModGroup(Mod mod, int groupIdx)
    {
        var group = mod._groups[groupIdx];
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.PrepareChange, mod, groupIdx, -1, -1);
        mod._groups.RemoveAt(groupIdx);
        UpdateSubModPositions(mod, groupIdx);
        _saveService.SaveAllOptionGroups(mod);
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.GroupDeleted, mod, groupIdx, -1, -1);
    }

    /// <summary> Move the index of a given option group. </summary>
    public void MoveModGroup(Mod mod, int groupIdxFrom, int groupIdxTo)
    {
        if (!mod._groups.Move(groupIdxFrom, groupIdxTo))
            return;

        UpdateSubModPositions(mod, Math.Min(groupIdxFrom, groupIdxTo));
        _saveService.SaveAllOptionGroups(mod);
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.GroupMoved, mod, groupIdxFrom, -1, groupIdxTo);
    }

    /// <summary> Change the description of the given option group. </summary>
    public void ChangeGroupDescription(Mod mod, int groupIdx, string newDescription)
    {
        var group = mod._groups[groupIdx];
        if (group.Description == newDescription)
            return;

        var _ = group switch
        {
            SingleModGroup s => s.Description = newDescription,
            MultiModGroup m  => m.Description = newDescription,
            _                => newDescription,
        };
        _saveService.QueueSave(new ModSaveGroup(mod, groupIdx));
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.DisplayChange, mod, groupIdx, -1, -1);
    }

    /// <summary> Change the description of the given option. </summary>
    public void ChangeOptionDescription(Mod mod, int groupIdx, int optionIdx, string newDescription)
    {
        var group  = mod._groups[groupIdx];
        var option = group[optionIdx];
        if (option.Description == newDescription || option is not SubMod s)
            return;

        s.Description = newDescription;
        _saveService.QueueSave(new ModSaveGroup(mod, groupIdx));
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.DisplayChange, mod, groupIdx, optionIdx, -1);
    }

    /// <summary> Change the internal priority of the given option group. </summary>
    public void ChangeGroupPriority(Mod mod, int groupIdx, int newPriority)
    {
        var group = mod._groups[groupIdx];
        if (group.Priority == newPriority)
            return;

        var _ = group switch
        {
            SingleModGroup s => s.Priority = newPriority,
            MultiModGroup m  => m.Priority = newPriority,
            _                => newPriority,
        };
        _saveService.QueueSave(new ModSaveGroup(mod, groupIdx));
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.PriorityChanged, mod, groupIdx, -1, -1);
    }

    /// <summary> Change the internal priority of the given option. </summary>
    public void ChangeOptionPriority(Mod mod, int groupIdx, int optionIdx, int newPriority)
    {
        switch (mod._groups[groupIdx])
        {
            case SingleModGroup:
                ChangeGroupPriority(mod, groupIdx, newPriority);
                break;
            case MultiModGroup m:
                if (m.PrioritizedOptions[optionIdx].Priority == newPriority)
                    return;

                m.PrioritizedOptions[optionIdx] = (m.PrioritizedOptions[optionIdx].Mod, newPriority);
                _saveService.QueueSave(new ModSaveGroup(mod, groupIdx));
                _communicator.ModOptionChanged.Invoke(ModOptionChangeType.PriorityChanged, mod, groupIdx, optionIdx, -1);
                return;
        }
    }

    /// <summary> Rename the given option. </summary>
    public void RenameOption(Mod mod, int groupIdx, int optionIdx, string newName)
    {
        switch (mod._groups[groupIdx])
        {
            case SingleModGroup s:
                if (s.OptionData[optionIdx].Name == newName)
                    return;

                s.OptionData[optionIdx].Name = newName;
                break;
            case MultiModGroup m:
                var option = m.PrioritizedOptions[optionIdx].Mod;
                if (option.Name == newName)
                    return;

                option.Name = newName;
                break;
        }

        _saveService.QueueSave(new ModSaveGroup(mod, groupIdx));
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.DisplayChange, mod, groupIdx, optionIdx, -1);
    }

    /// <summary> Add a new empty option of the given name for the given group. </summary>
    public void AddOption(Mod mod, int groupIdx, string newName)
    {
        var group  = mod._groups[groupIdx];
        var subMod = new SubMod(mod) { Name = newName };
        subMod.SetPosition(groupIdx, group.Count);
        switch (group)
        {
            case SingleModGroup s:
                s.OptionData.Add(subMod);
                break;
            case MultiModGroup m:
                m.PrioritizedOptions.Add((subMod, 0));
                break;
        }

        _saveService.QueueSave(new ModSaveGroup(mod, groupIdx));
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.OptionAdded, mod, groupIdx, group.Count - 1, -1);
    }

    /// <summary> Add an existing option to a given group with a given priority. </summary>
    public void AddOption(Mod mod, int groupIdx, ISubMod option, int priority = 0)
    {
        if (option is not SubMod o)
            return;

        var group = mod._groups[groupIdx];
        if (group.Type is GroupType.Multi && group.Count >= IModGroup.MaxMultiOptions)
        {
            Penumbra.Log.Error(
                $"Could not add option {option.Name} to {group.Name} for mod {mod.Name}, "
              + $"since only up to {IModGroup.MaxMultiOptions} options are supported in one group.");
            return;
        }

        o.SetPosition(groupIdx, group.Count);

        switch (group)
        {
            case SingleModGroup s:
                s.OptionData.Add(o);
                break;
            case MultiModGroup m:
                m.PrioritizedOptions.Add((o, priority));
                break;
        }

        _saveService.QueueSave(new ModSaveGroup(mod, groupIdx));
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.OptionAdded, mod, groupIdx, group.Count - 1, -1);
    }

    /// <summary> Delete the given option from the given group. </summary>
    public void DeleteOption(Mod mod, int groupIdx, int optionIdx)
    {
        var group = mod._groups[groupIdx];
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.PrepareChange, mod, groupIdx, optionIdx, -1);
        switch (group)
        {
            case SingleModGroup s:
                s.OptionData.RemoveAt(optionIdx);

                break;
            case MultiModGroup m:
                m.PrioritizedOptions.RemoveAt(optionIdx);
                break;
        }

        group.UpdatePositions(optionIdx);
        _saveService.QueueSave(new ModSaveGroup(mod, groupIdx));
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.OptionDeleted, mod, groupIdx, optionIdx, -1);
    }

    /// <summary> Move an option inside the given option group. </summary>
    public void MoveOption(Mod mod, int groupIdx, int optionIdxFrom, int optionIdxTo)
    {
        var group = mod._groups[groupIdx];
        if (!group.MoveOption(optionIdxFrom, optionIdxTo))
            return;

        _saveService.QueueSave(new ModSaveGroup(mod, groupIdx));
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.OptionMoved, mod, groupIdx, optionIdxFrom, optionIdxTo);
    }

    /// <summary> Set the meta manipulations for a given option. Replaces existing manipulations. </summary>
    public void OptionSetManipulations(Mod mod, int groupIdx, int optionIdx, HashSet<MetaManipulation> manipulations)
    {
        var subMod = GetSubMod(mod, groupIdx, optionIdx);
        if (subMod.Manipulations.Count == manipulations.Count
         && subMod.Manipulations.All(m => manipulations.TryGetValue(m, out var old) && old.EntryEquals(m)))
            return;

        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.PrepareChange, mod, groupIdx, optionIdx, -1);
        subMod.ManipulationData = manipulations;
        _saveService.QueueSave(new ModSaveGroup(mod, groupIdx));
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.OptionMetaChanged, mod, groupIdx, optionIdx, -1);
    }

    /// <summary> Set the file redirections for a given option. Replaces existing redirections. </summary>
    public void OptionSetFiles(Mod mod, int groupIdx, int optionIdx, Dictionary<Utf8GamePath, FullPath> replacements)
    {
        var subMod = GetSubMod(mod, groupIdx, optionIdx);
        if (subMod.FileData.SetEquals(replacements))
            return;

        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.PrepareChange, mod, groupIdx, optionIdx, -1);
        subMod.FileData = replacements;
        _saveService.QueueSave(new ModSaveGroup(mod, groupIdx));
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.OptionFilesChanged, mod, groupIdx, optionIdx, -1);
    }

    /// <summary> Add additional file redirections to a given option, keeping already existing ones. Only fires an event if anything is actually added.</summary>
    public void OptionAddFiles(Mod mod, int groupIdx, int optionIdx, Dictionary<Utf8GamePath, FullPath> additions)
    {
        var subMod   = GetSubMod(mod, groupIdx, optionIdx);
        var oldCount = subMod.FileData.Count;
        subMod.FileData.AddFrom(additions);
        if (oldCount != subMod.FileData.Count)
        {
            _saveService.QueueSave(new ModSaveGroup(mod, groupIdx));
            _communicator.ModOptionChanged.Invoke(ModOptionChangeType.OptionFilesAdded, mod, groupIdx, optionIdx, -1);
        }
    }

    /// <summary> Set the file swaps for a given option. Replaces existing swaps. </summary>
    public void OptionSetFileSwaps(Mod mod, int groupIdx, int optionIdx, Dictionary<Utf8GamePath, FullPath> swaps)
    {
        var subMod = GetSubMod(mod, groupIdx, optionIdx);
        if (subMod.FileSwapData.SetEquals(swaps))
            return;

        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.PrepareChange, mod, groupIdx, optionIdx, -1);
        subMod.FileSwapData = swaps;
        _saveService.QueueSave(new ModSaveGroup(mod, groupIdx));
        _communicator.ModOptionChanged.Invoke(ModOptionChangeType.OptionSwapsChanged, mod, groupIdx, optionIdx, -1);
    }


    /// <summary> Verify that a new option group name is unique in this mod. </summary>
    public static bool VerifyFileName(Mod mod, IModGroup? group, string newName, bool message)
    {
        var path = newName.RemoveInvalidPathSymbols();
        if (path.Length != 0
         && !mod.Groups.Any(o => !ReferenceEquals(o, group)
             && string.Equals(o.Name.RemoveInvalidPathSymbols(), path, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (message)
            Penumbra.ChatService.NotificationMessage(
                $"Could not name option {newName} because option with same filename {path} already exists.",
                "Warning", NotificationType.Warning);

        return false;
    }

    /// <summary> Update the indices stored in options from a given group on. </summary>
    private static void UpdateSubModPositions(Mod mod, int fromGroup)
    {
        foreach (var (group, groupIdx) in mod._groups.WithIndex().Skip(fromGroup))
        {
            foreach (var (o, optionIdx) in group.OfType<SubMod>().WithIndex())
                o.SetPosition(groupIdx, optionIdx);
        }
    }

    /// <summary> Get the correct option for the given group and option index. </summary>
    private static SubMod GetSubMod(Mod mod, int groupIdx, int optionIdx)
    {
        if (groupIdx == -1 && optionIdx == 0)
            return mod._default;

        return mod._groups[groupIdx] switch
        {
            SingleModGroup s => s.OptionData[optionIdx],
            MultiModGroup m  => m.PrioritizedOptions[optionIdx].Mod,
            _                => throw new InvalidOperationException(),
        };
    }
}
