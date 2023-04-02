using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Data;
using Newtonsoft.Json;
using OtterGui;
using Penumbra.Collections;
using Penumbra.Interop.PathResolving;
using Penumbra.Interop.Structs;
using Penumbra.Meta.Manipulations;
using Penumbra.Mods;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Penumbra.Api.Enums;
using Penumbra.GameData.Actors;
using Penumbra.Interop.ResourceLoading;
using Penumbra.Mods.Manager;
using Penumbra.String;
using Penumbra.String.Classes;
using Penumbra.Services;

namespace Penumbra.Api;

public class PenumbraApi : IDisposable, IPenumbraApi
{
    public (int, int) ApiVersion
        => (4, 19);

    private readonly Dictionary<ModCollection, ModCollection.ModSettingChangeDelegate> _delegates = new();

    public event Action<string>? PreSettingsPanelDraw;
    public event Action<string>? PostSettingsPanelDraw;

    public event GameObjectRedrawnDelegate? GameObjectRedrawn
    {
        add
        {
            CheckInitialized();
            _penumbra!.RedrawService.GameObjectRedrawn += value;
        }
        remove
        {
            CheckInitialized();
            _penumbra!.RedrawService.GameObjectRedrawn -= value;
        }
    }

    public event ModSettingChangedDelegate? ModSettingChanged;

    public event CreatingCharacterBaseDelegate? CreatingCharacterBase
    {
        add
        {
            if (value == null)
                return;

            CheckInitialized();
            _communicator.CreatingCharacterBase.Event += new Action<nint, string, nint, nint, nint>(value);
        }
        remove
        {
            if (value == null)
                return;

            CheckInitialized();
            _communicator.CreatingCharacterBase.Event -= new Action<nint, string, nint, nint, nint>(value);
        }
    }

    public event CreatedCharacterBaseDelegate? CreatedCharacterBase
    {
        add
        {
            if (value == null)
                return;

            CheckInitialized();
            _communicator.CreatedCharacterBase.Event += new Action<nint, string, nint>(value);
        }
        remove
        {
            if (value == null)
                return;

            CheckInitialized();
            _communicator.CreatedCharacterBase.Event -= new Action<nint, string, nint>(value);
        }
    }

    public bool Valid
        => _penumbra != null;

    private CommunicatorService _communicator;
    private Penumbra            _penumbra;
    private Lumina.GameData?    _lumina;

    private ModManager            _modManager;
    private ResourceLoader        _resourceLoader;
    private Configuration         _config;
    private CollectionManager     _collectionManager;
    private DalamudServices       _dalamud;
    private TempCollectionManager _tempCollections;
    private TempModManager        _tempMods;
    private ActorService          _actors;
    private CollectionResolver    _collectionResolver;
    private CutsceneService       _cutsceneService;

    public unsafe PenumbraApi(CommunicatorService communicator, Penumbra penumbra, ModManager modManager, ResourceLoader resourceLoader,
        Configuration config, CollectionManager collectionManager, DalamudServices dalamud, TempCollectionManager tempCollections,
        TempModManager tempMods, ActorService actors, CollectionResolver collectionResolver, CutsceneService cutsceneService)
    {
        _communicator       = communicator;
        _penumbra           = penumbra;
        _modManager         = modManager;
        _resourceLoader     = resourceLoader;
        _config             = config;
        _collectionManager  = collectionManager;
        _dalamud            = dalamud;
        _tempCollections    = tempCollections;
        _tempMods           = tempMods;
        _actors             = actors;
        _collectionResolver = collectionResolver;
        _cutsceneService    = cutsceneService;

        _lumina = (Lumina.GameData?)_dalamud.GameData.GetType()
            .GetField("gameData", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_dalamud.GameData);
        foreach (var collection in _collectionManager)
            SubscribeToCollection(collection);

        _communicator.CollectionChange.Event += SubscribeToNewCollections;
        _resourceLoader.ResourceLoaded       += OnResourceLoaded;
        _communicator.ModPathChanged.Event   += ModPathChangeSubscriber;
    }

    public unsafe void Dispose()
    {
        if (!Valid)
            return;

        foreach (var collection in _collectionManager)
        {
            if (_delegates.TryGetValue(collection, out var del))
                collection.ModSettingChanged -= del;
        }

        _resourceLoader.ResourceLoaded       -= OnResourceLoaded;
        _communicator.CollectionChange.Event -= SubscribeToNewCollections;
        _communicator.ModPathChanged.Event   -= ModPathChangeSubscriber;
        _lumina                              =  null;
        _communicator                        =  null!;
        _penumbra                            =  null!;
        _modManager                          =  null!;
        _resourceLoader                      =  null!;
        _config                              =  null!;
        _collectionManager                   =  null!;
        _dalamud                             =  null!;
        _tempCollections                     =  null!;
        _tempMods                            =  null!;
        _actors                              =  null!;
        _collectionResolver                  =  null!;
        _cutsceneService                     =  null!;
    }

    public event ChangedItemClick? ChangedItemClicked;

    public string GetModDirectory()
    {
        CheckInitialized();
        return _config.ModDirectory;
    }

    private unsafe void OnResourceLoaded(ResourceHandle* _, Utf8GamePath originalPath, FullPath? manipulatedPath,
        ResolveData resolveData)
    {
        if (resolveData.AssociatedGameObject != nint.Zero)
            GameObjectResourceResolved?.Invoke(resolveData.AssociatedGameObject, originalPath.ToString(),
                manipulatedPath?.ToString() ?? originalPath.ToString());
    }

    public event Action<string, bool>? ModDirectoryChanged
    {
        add
        {
            CheckInitialized();
            _communicator.ModDirectoryChanged.Event += value;
        }
        remove
        {
            CheckInitialized();
            _communicator.ModDirectoryChanged.Event -= value;
        }
    }

    public bool GetEnabledState()
        => _config.EnableMods;

    public event Action<bool>? EnabledChange
    {
        add
        {
            CheckInitialized();
            _penumbra!.EnabledChange += value;
        }
        remove
        {
            CheckInitialized();
            _penumbra!.EnabledChange -= value;
        }
    }

    public string GetConfiguration()
    {
        CheckInitialized();
        return JsonConvert.SerializeObject(_config, Formatting.Indented);
    }

    public event ChangedItemHover?                   ChangedItemTooltip;
    public event GameObjectResourceResolvedDelegate? GameObjectResourceResolved;

    public PenumbraApiEc OpenMainWindow(TabType tab, string modDirectory, string modName)
    {
        CheckInitialized();
        if (_penumbra!.ConfigWindow == null)
            return PenumbraApiEc.SystemDisposed;

        _penumbra!.ConfigWindow.IsOpen = true;

        if (!Enum.IsDefined(tab))
            return PenumbraApiEc.InvalidArgument;

        if (tab != TabType.None)
            _penumbra!.ConfigWindow.SelectTab(tab);

        if (tab == TabType.Mods && (modDirectory.Length > 0 || modName.Length > 0))
        {
            if (_modManager.TryGetMod(modDirectory, modName, out var mod))
                _penumbra!.ConfigWindow.SelectMod(mod);
            else
                return PenumbraApiEc.ModMissing;
        }

        return PenumbraApiEc.Success;
    }

    public void CloseMainWindow()
    {
        CheckInitialized();
        if (_penumbra!.ConfigWindow == null)
            return;

        _penumbra!.ConfigWindow.IsOpen = false;
    }

    public void RedrawObject(int tableIndex, RedrawType setting)
    {
        CheckInitialized();
        _penumbra!.RedrawService.RedrawObject(tableIndex, setting);
    }

    public void RedrawObject(string name, RedrawType setting)
    {
        CheckInitialized();
        _penumbra!.RedrawService.RedrawObject(name, setting);
    }

    public void RedrawObject(GameObject? gameObject, RedrawType setting)
    {
        CheckInitialized();
        _penumbra!.RedrawService.RedrawObject(gameObject, setting);
    }

    public void RedrawAll(RedrawType setting)
    {
        CheckInitialized();
        _penumbra!.RedrawService.RedrawAll(setting);
    }

    public string ResolveDefaultPath(string path)
    {
        CheckInitialized();
        return ResolvePath(path, _modManager, _collectionManager.Default);
    }

    public string ResolveInterfacePath(string path)
    {
        CheckInitialized();
        return ResolvePath(path, _modManager, _collectionManager.Interface);
    }

    public string ResolvePlayerPath(string path)
    {
        CheckInitialized();
        return ResolvePath(path, _modManager, _collectionResolver.PlayerCollection());
    }

    // TODO: cleanup when incrementing API level
    public string ResolvePath(string path, string characterName)
        => ResolvePath(path, characterName, ushort.MaxValue);

    public string ResolveGameObjectPath(string path, int gameObjectIdx)
    {
        CheckInitialized();
        AssociatedCollection(gameObjectIdx, out var collection);
        return ResolvePath(path, _modManager, collection);
    }

    public string ResolvePath(string path, string characterName, ushort worldId)
    {
        CheckInitialized();
        return ResolvePath(path, _modManager,
            _collectionManager.Individual(NameToIdentifier(characterName, worldId)));
    }

    // TODO: cleanup when incrementing API level
    public string[] ReverseResolvePath(string path, string characterName)
        => ReverseResolvePath(path, characterName, ushort.MaxValue);

    public string[] ReverseResolvePath(string path, string characterName, ushort worldId)
    {
        CheckInitialized();
        if (!_config.EnableMods)
            return new[]
            {
                path,
            };

        var ret = _collectionManager.Individual(NameToIdentifier(characterName, worldId)).ReverseResolvePath(new FullPath(path));
        return ret.Select(r => r.ToString()).ToArray();
    }

    public string[] ReverseResolveGameObjectPath(string path, int gameObjectIdx)
    {
        CheckInitialized();
        if (!_config.EnableMods)
            return new[]
            {
                path,
            };

        AssociatedCollection(gameObjectIdx, out var collection);
        var ret = collection.ReverseResolvePath(new FullPath(path));
        return ret.Select(r => r.ToString()).ToArray();
    }

    public string[] ReverseResolvePlayerPath(string path)
    {
        CheckInitialized();
        if (!_config.EnableMods)
            return new[]
            {
                path,
            };

        var ret = _collectionResolver.PlayerCollection().ReverseResolvePath(new FullPath(path));
        return ret.Select(r => r.ToString()).ToArray();
    }

    public (string[], string[][]) ResolvePlayerPaths(string[] forward, string[] reverse)
    {
        CheckInitialized();
        if (!_config.EnableMods)
            return (forward, reverse.Select(p => new[]
            {
                p,
            }).ToArray());

        var playerCollection = _collectionResolver.PlayerCollection();
        var resolved         = forward.Select(p => ResolvePath(p, _modManager, playerCollection)).ToArray();
        var reverseResolved  = playerCollection.ReverseResolvePaths(reverse);
        return (resolved, reverseResolved.Select(a => a.Select(p => p.ToString()).ToArray()).ToArray());
    }

    public T? GetFile<T>(string gamePath) where T : FileResource
        => GetFileIntern<T>(ResolveDefaultPath(gamePath));

    public T? GetFile<T>(string gamePath, string characterName) where T : FileResource
        => GetFileIntern<T>(ResolvePath(gamePath, characterName));

    public IReadOnlyDictionary<string, object?> GetChangedItemsForCollection(string collectionName)
    {
        CheckInitialized();
        try
        {
            if (!_collectionManager.ByName(collectionName, out var collection))
                collection = ModCollection.Empty;

            if (collection.HasCache)
                return collection.ChangedItems.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Item2);

            Penumbra.Log.Warning($"Collection {collectionName} does not exist or is not loaded.");
            return new Dictionary<string, object?>();
        }
        catch (Exception e)
        {
            Penumbra.Log.Error($"Could not obtain Changed Items for {collectionName}:\n{e}");
            throw;
        }
    }

    public string GetCollectionForType(ApiCollectionType type)
    {
        CheckInitialized();
        if (!Enum.IsDefined(type))
            return string.Empty;

        var collection = _collectionManager.ByType((CollectionType)type);
        return collection?.Name ?? string.Empty;
    }

    public (PenumbraApiEc, string OldCollection) SetCollectionForType(ApiCollectionType type, string collectionName, bool allowCreateNew,
        bool allowDelete)
    {
        CheckInitialized();
        if (!Enum.IsDefined(type))
            return (PenumbraApiEc.InvalidArgument, string.Empty);

        var oldCollection = _collectionManager.ByType((CollectionType)type)?.Name ?? string.Empty;

        if (collectionName.Length == 0)
        {
            if (oldCollection.Length == 0)
                return (PenumbraApiEc.NothingChanged, oldCollection);

            if (!allowDelete || type is ApiCollectionType.Current or ApiCollectionType.Default or ApiCollectionType.Interface)
                return (PenumbraApiEc.AssignmentDeletionDisallowed, oldCollection);

            _collectionManager.RemoveSpecialCollection((CollectionType)type);
            return (PenumbraApiEc.Success, oldCollection);
        }

        if (!_collectionManager.ByName(collectionName, out var collection))
            return (PenumbraApiEc.CollectionMissing, oldCollection);

        if (oldCollection.Length == 0)
        {
            if (!allowCreateNew)
                return (PenumbraApiEc.AssignmentCreationDisallowed, oldCollection);

            _collectionManager.CreateSpecialCollection((CollectionType)type);
        }
        else if (oldCollection == collection.Name)
        {
            return (PenumbraApiEc.NothingChanged, oldCollection);
        }

        _collectionManager.SetCollection(collection, (CollectionType)type);
        return (PenumbraApiEc.Success, oldCollection);
    }

    public (bool ObjectValid, bool IndividualSet, string EffectiveCollection) GetCollectionForObject(int gameObjectIdx)
    {
        CheckInitialized();
        var id = AssociatedIdentifier(gameObjectIdx);
        if (!id.IsValid)
            return (false, false, _collectionManager.Default.Name);

        if (_collectionManager.Individuals.Individuals.TryGetValue(id, out var collection))
            return (true, true, collection.Name);

        AssociatedCollection(gameObjectIdx, out collection);
        return (true, false, collection.Name);
    }

    public (PenumbraApiEc, string OldCollection) SetCollectionForObject(int gameObjectIdx, string collectionName, bool allowCreateNew,
        bool allowDelete)
    {
        CheckInitialized();
        var id = AssociatedIdentifier(gameObjectIdx);
        if (!id.IsValid)
            return (PenumbraApiEc.InvalidIdentifier, _collectionManager.Default.Name);

        var oldCollection = _collectionManager.Individuals.Individuals.TryGetValue(id, out var c) ? c.Name : string.Empty;

        if (collectionName.Length == 0)
        {
            if (oldCollection.Length == 0)
                return (PenumbraApiEc.NothingChanged, oldCollection);

            if (!allowDelete)
                return (PenumbraApiEc.AssignmentDeletionDisallowed, oldCollection);

            var idx = _collectionManager.Individuals.Index(id);
            _collectionManager.RemoveIndividualCollection(idx);
            return (PenumbraApiEc.Success, oldCollection);
        }

        if (!_collectionManager.ByName(collectionName, out var collection))
            return (PenumbraApiEc.CollectionMissing, oldCollection);

        if (oldCollection.Length == 0)
        {
            if (!allowCreateNew)
                return (PenumbraApiEc.AssignmentCreationDisallowed, oldCollection);

            var ids = _collectionManager.Individuals.GetGroup(id);
            _collectionManager.CreateIndividualCollection(ids);
        }
        else if (oldCollection == collection.Name)
        {
            return (PenumbraApiEc.NothingChanged, oldCollection);
        }

        _collectionManager.SetCollection(collection, CollectionType.Individual, _collectionManager.Individuals.Index(id));
        return (PenumbraApiEc.Success, oldCollection);
    }

    public IList<string> GetCollections()
    {
        CheckInitialized();
        return _collectionManager.Select(c => c.Name).ToArray();
    }

    public string GetCurrentCollection()
    {
        CheckInitialized();
        return _collectionManager.Current.Name;
    }

    public string GetDefaultCollection()
    {
        CheckInitialized();
        return _collectionManager.Default.Name;
    }

    public string GetInterfaceCollection()
    {
        CheckInitialized();
        return _collectionManager.Interface.Name;
    }

    // TODO: cleanup when incrementing API level
    public (string, bool) GetCharacterCollection(string characterName)
        => GetCharacterCollection(characterName, ushort.MaxValue);

    public (string, bool) GetCharacterCollection(string characterName, ushort worldId)
    {
        CheckInitialized();
        return _collectionManager.Individuals.TryGetCollection(NameToIdentifier(characterName, worldId), out var collection)
            ? (collection.Name, true)
            : (_collectionManager.Default.Name, false);
    }

    public unsafe (nint, string) GetDrawObjectInfo(nint drawObject)
    {
        CheckInitialized();
        var data = _collectionResolver.IdentifyCollection((DrawObject*)drawObject, true);
        return (data.AssociatedGameObject, data.ModCollection.Name);
    }

    public int GetCutsceneParentIndex(int actorIdx)
    {
        CheckInitialized();
        return _cutsceneService.GetParentIndex(actorIdx);
    }

    public IList<(string, string)> GetModList()
    {
        CheckInitialized();
        return _modManager.Select(m => (m.ModPath.Name, m.Name.Text)).ToArray();
    }

    public IDictionary<string, (IList<string>, GroupType)>? GetAvailableModSettings(string modDirectory, string modName)
    {
        CheckInitialized();
        return _modManager.TryGetMod(modDirectory, modName, out var mod)
            ? mod.Groups.ToDictionary(g => g.Name, g => ((IList<string>)g.Select(o => o.Name).ToList(), g.Type))
            : null;
    }

    public (PenumbraApiEc, (bool, int, IDictionary<string, IList<string>>, bool)?) GetCurrentModSettings(string collectionName,
        string modDirectory, string modName, bool allowInheritance)
    {
        CheckInitialized();
        if (!_collectionManager.ByName(collectionName, out var collection))
            return (PenumbraApiEc.CollectionMissing, null);

        if (!_modManager.TryGetMod(modDirectory, modName, out var mod))
            return (PenumbraApiEc.ModMissing, null);

        var settings = allowInheritance ? collection.Settings[mod.Index] : collection[mod.Index].Settings;
        if (settings == null)
            return (PenumbraApiEc.Success, null);

        var shareSettings = settings.ConvertToShareable(mod);
        return (PenumbraApiEc.Success,
            (shareSettings.Enabled, shareSettings.Priority, shareSettings.Settings, collection.Settings[mod.Index] != null));
    }
    public PenumbraApiEc UnpackMod(string modpackFile)
    {
        if (File.Exists(modpackFile))
        {
            ExternalModImporter.ModFileSystemSelectorInstance.ImportStandaloneModPackage(modpackFile);
            return PenumbraApiEc.Success;
        }
        else
        {
            return PenumbraApiEc.FileMissing;
        }
    }

    public PenumbraApiEc ReloadMod(string modDirectory, string modName)
    {
        CheckInitialized();
        if (!_modManager.TryGetMod(modDirectory, modName, out var mod))
            return PenumbraApiEc.ModMissing;

        _modManager.ReloadMod(mod);
        return PenumbraApiEc.Success;
    }

    public PenumbraApiEc AddMod(string modDirectory)
    {
        CheckInitialized();
        var dir = new DirectoryInfo(Path.Join(_modManager.BasePath.FullName, Path.GetFileName(modDirectory)));
        if (!dir.Exists)
            return PenumbraApiEc.FileMissing;

        _modManager.AddMod(dir);
        return PenumbraApiEc.Success;
    }

    public PenumbraApiEc DeleteMod(string modDirectory, string modName)
    {
        CheckInitialized();
        if (!_modManager.TryGetMod(modDirectory, modName, out var mod))
            return PenumbraApiEc.NothingChanged;

        _modManager.DeleteMod(mod);
        return PenumbraApiEc.Success;
    }

    public event Action<string>?         ModDeleted;
    public event Action<string>?         ModAdded;
    public event Action<string, string>? ModMoved;

    private void ModPathChangeSubscriber(ModPathChangeType type, Mod mod, DirectoryInfo? oldDirectory,
        DirectoryInfo? newDirectory)
    {
        switch (type)
        {
            case ModPathChangeType.Deleted when oldDirectory != null:
                ModDeleted?.Invoke(oldDirectory.Name);
                break;
            case ModPathChangeType.Added when newDirectory != null:
                ModAdded?.Invoke(newDirectory.Name);
                break;
            case ModPathChangeType.Moved when newDirectory != null && oldDirectory != null:
                ModMoved?.Invoke(oldDirectory.Name, newDirectory.Name);
                break;
        }
    }

    public (PenumbraApiEc, string, bool) GetModPath(string modDirectory, string modName)
    {
        CheckInitialized();
        if (!_modManager.TryGetMod(modDirectory, modName, out var mod)
         || !_penumbra!.ModFileSystem.FindLeaf(mod, out var leaf))
            return (PenumbraApiEc.ModMissing, string.Empty, false);

        var fullPath = leaf.FullName();

        return (PenumbraApiEc.Success, fullPath, !ModFileSystem.ModHasDefaultPath(mod, fullPath));
    }

    public PenumbraApiEc SetModPath(string modDirectory, string modName, string newPath)
    {
        CheckInitialized();
        if (newPath.Length == 0)
            return PenumbraApiEc.InvalidArgument;

        if (!_modManager.TryGetMod(modDirectory, modName, out var mod)
         || !_penumbra!.ModFileSystem.FindLeaf(mod, out var leaf))
            return PenumbraApiEc.ModMissing;

        try
        {
            _penumbra.ModFileSystem.RenameAndMove(leaf, newPath);
            return PenumbraApiEc.Success;
        }
        catch
        {
            return PenumbraApiEc.PathRenameFailed;
        }
    }

    public PenumbraApiEc TryInheritMod(string collectionName, string modDirectory, string modName, bool inherit)
    {
        CheckInitialized();
        if (!_collectionManager.ByName(collectionName, out var collection))
            return PenumbraApiEc.CollectionMissing;

        if (!_modManager.TryGetMod(modDirectory, modName, out var mod))
            return PenumbraApiEc.ModMissing;


        return collection.SetModInheritance(mod.Index, inherit) ? PenumbraApiEc.Success : PenumbraApiEc.NothingChanged;
    }

    public PenumbraApiEc TrySetMod(string collectionName, string modDirectory, string modName, bool enabled)
    {
        CheckInitialized();
        if (!_collectionManager.ByName(collectionName, out var collection))
            return PenumbraApiEc.CollectionMissing;

        if (!_modManager.TryGetMod(modDirectory, modName, out var mod))
            return PenumbraApiEc.ModMissing;

        return collection.SetModState(mod.Index, enabled) ? PenumbraApiEc.Success : PenumbraApiEc.NothingChanged;
    }

    public PenumbraApiEc TrySetModPriority(string collectionName, string modDirectory, string modName, int priority)
    {
        CheckInitialized();
        if (!_collectionManager.ByName(collectionName, out var collection))
            return PenumbraApiEc.CollectionMissing;

        if (!_modManager.TryGetMod(modDirectory, modName, out var mod))
            return PenumbraApiEc.ModMissing;

        return collection.SetModPriority(mod.Index, priority) ? PenumbraApiEc.Success : PenumbraApiEc.NothingChanged;
    }

    public PenumbraApiEc TrySetModSetting(string collectionName, string modDirectory, string modName, string optionGroupName,
        string optionName)
    {
        CheckInitialized();
        if (!_collectionManager.ByName(collectionName, out var collection))
            return PenumbraApiEc.CollectionMissing;

        if (!_modManager.TryGetMod(modDirectory, modName, out var mod))
            return PenumbraApiEc.ModMissing;

        var groupIdx = mod.Groups.IndexOf(g => g.Name == optionGroupName);
        if (groupIdx < 0)
            return PenumbraApiEc.OptionGroupMissing;

        var optionIdx = mod.Groups[groupIdx].IndexOf(o => o.Name == optionName);
        if (optionIdx < 0)
            return PenumbraApiEc.OptionMissing;

        var setting = mod.Groups[groupIdx].Type == GroupType.Multi ? 1u << optionIdx : (uint)optionIdx;

        return collection.SetModSetting(mod.Index, groupIdx, setting) ? PenumbraApiEc.Success : PenumbraApiEc.NothingChanged;
    }

    public PenumbraApiEc TrySetModSettings(string collectionName, string modDirectory, string modName, string optionGroupName,
        IReadOnlyList<string> optionNames)
    {
        CheckInitialized();
        if (!_collectionManager.ByName(collectionName, out var collection))
            return PenumbraApiEc.CollectionMissing;

        if (!_modManager.TryGetMod(modDirectory, modName, out var mod))
            return PenumbraApiEc.ModMissing;

        var groupIdx = mod.Groups.IndexOf(g => g.Name == optionGroupName);
        if (groupIdx < 0)
            return PenumbraApiEc.OptionGroupMissing;

        var group = mod.Groups[groupIdx];

        uint setting = 0;
        if (group.Type == GroupType.Single)
        {
            var optionIdx = optionNames.Count == 0 ? -1 : group.IndexOf(o => o.Name == optionNames[^1]);
            if (optionIdx < 0)
                return PenumbraApiEc.OptionMissing;

            setting = (uint)optionIdx;
        }
        else
        {
            foreach (var name in optionNames)
            {
                var optionIdx = group.IndexOf(o => o.Name == name);
                if (optionIdx < 0)
                    return PenumbraApiEc.OptionMissing;

                setting |= 1u << optionIdx;
            }
        }

        return collection.SetModSetting(mod.Index, groupIdx, setting) ? PenumbraApiEc.Success : PenumbraApiEc.NothingChanged;
    }


    public PenumbraApiEc CopyModSettings(string? collectionName, string modDirectoryFrom, string modDirectoryTo)
    {
        CheckInitialized();

        var sourceModIdx = _modManager
                .FirstOrDefault(m => string.Equals(m.ModPath.Name, modDirectoryFrom, StringComparison.OrdinalIgnoreCase))?.Index
         ?? -1;
        var targetModIdx = _modManager
                .FirstOrDefault(m => string.Equals(m.ModPath.Name, modDirectoryTo, StringComparison.OrdinalIgnoreCase))?.Index
         ?? -1;
        if (string.IsNullOrEmpty(collectionName))
            foreach (var collection in _collectionManager)
                collection.CopyModSettings(sourceModIdx, modDirectoryFrom, targetModIdx, modDirectoryTo);
        else if (_collectionManager.ByName(collectionName, out var collection))
            collection.CopyModSettings(sourceModIdx, modDirectoryFrom, targetModIdx, modDirectoryTo);
        else
            return PenumbraApiEc.CollectionMissing;

        return PenumbraApiEc.Success;
    }

    public (PenumbraApiEc, string) CreateTemporaryCollection(string tag, string character, bool forceOverwriteCharacter)
    {
        CheckInitialized();

        if (!ActorManager.VerifyPlayerName(character.AsSpan()) || tag.Length == 0)
            return (PenumbraApiEc.InvalidArgument, string.Empty);

        var identifier = NameToIdentifier(character, ushort.MaxValue);
        if (!identifier.IsValid)
            return (PenumbraApiEc.InvalidArgument, string.Empty);

        if (!forceOverwriteCharacter && _collectionManager.Individuals.Individuals.ContainsKey(identifier)
         || _tempCollections.Collections.Individuals.ContainsKey(identifier))
            return (PenumbraApiEc.CharacterCollectionExists, string.Empty);

        var name = $"{tag}_{character}";
        var ret  = CreateNamedTemporaryCollection(name);
        if (ret != PenumbraApiEc.Success)
            return (ret, name);

        if (_tempCollections.AddIdentifier(name, identifier))
            return (PenumbraApiEc.Success, name);

        _tempCollections.RemoveTemporaryCollection(name);
        return (PenumbraApiEc.UnknownError, string.Empty);
    }

    public PenumbraApiEc CreateNamedTemporaryCollection(string name)
    {
        CheckInitialized();
        if (name.Length == 0 || ModCreator.ReplaceBadXivSymbols(name) != name)
            return PenumbraApiEc.InvalidArgument;

        return _tempCollections.CreateTemporaryCollection(name).Length > 0
            ? PenumbraApiEc.Success
            : PenumbraApiEc.CollectionExists;
    }

    public PenumbraApiEc AssignTemporaryCollection(string collectionName, int actorIndex, bool forceAssignment)
    {
        CheckInitialized();

        if (!_actors.Valid)
            return PenumbraApiEc.SystemDisposed;

        if (actorIndex < 0 || actorIndex >= _dalamud.Objects.Length)
            return PenumbraApiEc.InvalidArgument;

        var identifier = _actors.AwaitedService.FromObject(_dalamud.Objects[actorIndex], false, false, true);
        if (!identifier.IsValid)
            return PenumbraApiEc.InvalidArgument;

        if (!_tempCollections.CollectionByName(collectionName, out var collection))
            return PenumbraApiEc.CollectionMissing;

        if (forceAssignment)
        {
            if (_tempCollections.Collections.Individuals.ContainsKey(identifier) && !_tempCollections.Collections.Delete(identifier))
                return PenumbraApiEc.AssignmentDeletionFailed;
        }
        else if (_tempCollections.Collections.Individuals.ContainsKey(identifier)
              || _collectionManager.Individuals.Individuals.ContainsKey(identifier))
        {
            return PenumbraApiEc.CharacterCollectionExists;
        }

        var group = _tempCollections.Collections.GetGroup(identifier);
        return _tempCollections.AddIdentifier(collection, group)
            ? PenumbraApiEc.Success
            : PenumbraApiEc.UnknownError;
    }

    public PenumbraApiEc RemoveTemporaryCollection(string character)
    {
        CheckInitialized();
        return _tempCollections.RemoveByCharacterName(character)
            ? PenumbraApiEc.Success
            : PenumbraApiEc.NothingChanged;
    }

    public PenumbraApiEc RemoveTemporaryCollectionByName(string name)
    {
        CheckInitialized();
        return _tempCollections.RemoveTemporaryCollection(name)
            ? PenumbraApiEc.Success
            : PenumbraApiEc.NothingChanged;
    }

    public PenumbraApiEc AddTemporaryModAll(string tag, Dictionary<string, string> paths, string manipString, int priority)
    {
        CheckInitialized();
        if (!ConvertPaths(paths, out var p))
            return PenumbraApiEc.InvalidGamePath;

        if (!ConvertManips(manipString, out var m))
            return PenumbraApiEc.InvalidManipulation;

        return _tempMods.Register(tag, null, p, m, priority) switch
        {
            RedirectResult.Success => PenumbraApiEc.Success,
            _                      => PenumbraApiEc.UnknownError,
        };
    }

    public PenumbraApiEc AddTemporaryMod(string tag, string collectionName, Dictionary<string, string> paths, string manipString,
        int priority)
    {
        CheckInitialized();
        if (!_tempCollections.CollectionByName(collectionName, out var collection)
         && !_collectionManager.ByName(collectionName, out collection))
            return PenumbraApiEc.CollectionMissing;

        if (!ConvertPaths(paths, out var p))
            return PenumbraApiEc.InvalidGamePath;

        if (!ConvertManips(manipString, out var m))
            return PenumbraApiEc.InvalidManipulation;

        return _tempMods.Register(tag, collection, p, m, priority) switch
        {
            RedirectResult.Success => PenumbraApiEc.Success,
            _                      => PenumbraApiEc.UnknownError,
        };
    }

    public PenumbraApiEc RemoveTemporaryModAll(string tag, int priority)
    {
        CheckInitialized();
        return _tempMods.Unregister(tag, null, priority) switch
        {
            RedirectResult.Success       => PenumbraApiEc.Success,
            RedirectResult.NotRegistered => PenumbraApiEc.NothingChanged,
            _                            => PenumbraApiEc.UnknownError,
        };
    }

    public PenumbraApiEc RemoveTemporaryMod(string tag, string collectionName, int priority)
    {
        CheckInitialized();
        if (!_tempCollections.CollectionByName(collectionName, out var collection)
         && !_collectionManager.ByName(collectionName, out collection))
            return PenumbraApiEc.CollectionMissing;

        return _tempMods.Unregister(tag, collection, priority) switch
        {
            RedirectResult.Success       => PenumbraApiEc.Success,
            RedirectResult.NotRegistered => PenumbraApiEc.NothingChanged,
            _                            => PenumbraApiEc.UnknownError,
        };
    }

    public string GetPlayerMetaManipulations()
    {
        CheckInitialized();
        var collection = _collectionResolver.PlayerCollection();
        var set        = collection.MetaCache?.Manipulations.ToArray() ?? Array.Empty<MetaManipulation>();
        return Functions.ToCompressedBase64(set, MetaManipulation.CurrentVersion);
    }

    // TODO: cleanup when incrementing API
    public string GetMetaManipulations(string characterName)
        => GetMetaManipulations(characterName, ushort.MaxValue);

    public string GetMetaManipulations(string characterName, ushort worldId)
    {
        CheckInitialized();
        var identifier = NameToIdentifier(characterName, worldId);
        var collection = _tempCollections.Collections.TryGetCollection(identifier, out var c)
            ? c
            : _collectionManager.Individual(identifier);
        var set = collection.MetaCache?.Manipulations.ToArray() ?? Array.Empty<MetaManipulation>();
        return Functions.ToCompressedBase64(set, MetaManipulation.CurrentVersion);
    }

    public string GetGameObjectMetaManipulations(int gameObjectIdx)
    {
        CheckInitialized();
        AssociatedCollection(gameObjectIdx, out var collection);
        var set = collection.MetaCache?.Manipulations.ToArray() ?? Array.Empty<MetaManipulation>();
        return Functions.ToCompressedBase64(set, MetaManipulation.CurrentVersion);
    }

    internal bool HasTooltip
        => ChangedItemTooltip != null;

    internal void InvokeTooltip(object? it)
        => ChangedItemTooltip?.Invoke(it);

    internal void InvokeClick(MouseButton button, object? it)
        => ChangedItemClicked?.Invoke(button, it);


    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void CheckInitialized()
    {
        if (!Valid)
            throw new Exception("PluginShare is not initialized.");
    }

    // Return the collection associated to a current game object. If it does not exist, return the default collection.
    // If the index is invalid, returns false and the default collection.
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe bool AssociatedCollection(int gameObjectIdx, out ModCollection collection)
    {
        collection = _collectionManager.Default;
        if (gameObjectIdx < 0 || gameObjectIdx >= _dalamud.Objects.Length)
            return false;

        var ptr  = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)_dalamud.Objects.GetObjectAddress(gameObjectIdx);
        var data = _collectionResolver.IdentifyCollection(ptr, false);
        if (data.Valid)
            collection = data.ModCollection;

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe ActorIdentifier AssociatedIdentifier(int gameObjectIdx)
    {
        if (gameObjectIdx < 0 || gameObjectIdx >= _dalamud.Objects.Length || !_actors.Valid)
            return ActorIdentifier.Invalid;

        var ptr = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)_dalamud.Objects.GetObjectAddress(gameObjectIdx);
        return _actors.AwaitedService.FromObject(ptr, out _, false, true, true);
    }

    // Resolve a path given by string for a specific collection.
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private string ResolvePath(string path, ModManager _, ModCollection collection)
    {
        if (!_config.EnableMods)
            return path;

        var gamePath = Utf8GamePath.FromString(path, out var p, true) ? p : Utf8GamePath.Empty;
        var ret      = collection.ResolvePath(gamePath);
        return ret?.ToString() ?? path;
    }

    // Get a file for a resolved path.
    private T? GetFileIntern<T>(string resolvedPath) where T : FileResource
    {
        CheckInitialized();
        try
        {
            if (Path.IsPathRooted(resolvedPath))
                return _lumina?.GetFileFromDisk<T>(resolvedPath);

            return _dalamud.GameData.GetFile<T>(resolvedPath);
        }
        catch (Exception e)
        {
            Penumbra.Log.Warning($"Could not load file {resolvedPath}:\n{e}");
            return null;
        }
    }


    // Convert a dictionary of strings to a dictionary of gamepaths to full paths.
    // Only returns true if all paths can successfully be converted and added.
    private static bool ConvertPaths(IReadOnlyDictionary<string, string> redirections,
        [NotNullWhen(true)] out Dictionary<Utf8GamePath, FullPath>? paths)
    {
        paths = new Dictionary<Utf8GamePath, FullPath>(redirections.Count);
        foreach (var (gString, fString) in redirections)
        {
            if (!Utf8GamePath.FromString(gString, out var path, false))
            {
                paths = null;
                return false;
            }

            var fullPath = new FullPath(fString);
            if (!paths.TryAdd(path, fullPath))
            {
                paths = null;
                return false;
            }
        }

        return true;
    }

    // Convert manipulations from a transmitted base64 string to actual manipulations.
    // The empty string is treated as an empty set.
    // Only returns true if all conversions are successful and distinct.
    private static bool ConvertManips(string manipString,
        [NotNullWhen(true)] out HashSet<MetaManipulation>? manips)
    {
        if (manipString.Length == 0)
        {
            manips = new HashSet<MetaManipulation>();
            return true;
        }

        if (Functions.FromCompressedBase64<MetaManipulation[]>(manipString, out var manipArray) != MetaManipulation.CurrentVersion)
        {
            manips = null;
            return false;
        }

        manips = new HashSet<MetaManipulation>(manipArray!.Length);
        foreach (var manip in manipArray.Where(m => m.ManipulationType != MetaManipulation.Type.Unknown))
        {
            if (!manips.Add(manip))
            {
                manips = null;
                return false;
            }
        }

        return true;
    }

    private void SubscribeToCollection(ModCollection c)
    {
        var name = c.Name;

        void Del(ModSettingChange type, int idx, int _, int _2, bool inherited)
            => ModSettingChanged?.Invoke(type, name, idx >= 0 ? _modManager[idx].ModPath.Name : string.Empty, inherited);

        _delegates[c]       =  Del;
        c.ModSettingChanged += Del;
    }

    private void SubscribeToNewCollections(CollectionType type, ModCollection? oldCollection, ModCollection? newCollection, string _)
    {
        if (type != CollectionType.Inactive)
            return;

        if (oldCollection != null && _delegates.TryGetValue(oldCollection, out var del))
            oldCollection.ModSettingChanged -= del;

        if (newCollection != null)
            SubscribeToCollection(newCollection);
    }

    public void InvokePreSettingsPanel(string modDirectory)
        => PreSettingsPanelDraw?.Invoke(modDirectory);

    public void InvokePostSettingsPanel(string modDirectory)
        => PostSettingsPanelDraw?.Invoke(modDirectory);

    // TODO: replace all usages with ActorIdentifier stuff when incrementing API
    private ActorIdentifier NameToIdentifier(string name, ushort worldId)
    {
        if (!_actors.Valid)
            return ActorIdentifier.Invalid;

        // Verified to be valid name beforehand.
        var b = ByteString.FromStringUnsafe(name, false);
        return _actors.AwaitedService.CreatePlayer(b, worldId);
    }
}
