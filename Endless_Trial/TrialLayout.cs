using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Mirror;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Tilemaps;

namespace SephiriaTrial
{
    // 레거시 TrialLayout.json의 형식이다. XML 레이아웃이 없을 때만 호환용으로 쓴다.
    // 타일 ID는 Sephiria의 TileDatabase에 등록된 기존 게임 타일 ID만 사용한다.
    [Serializable]
    public sealed class TrialLayoutData
    {
        public int version = 1;
        public string name = "Deep Cave Trial Arena";
        public int width = 26;
        public int height = 18;
        public TrialTileRect[] ground = Array.Empty<TrialTileRect>();
        public TrialTileRect[] upperGround = Array.Empty<TrialTileRect>();
        public TrialTileRect[] wall = Array.Empty<TrialTileRect>();
        public TrialTileRect[] cliff = Array.Empty<TrialTileRect>();
        public TrialLayoutProp[] props = Array.Empty<TrialLayoutProp>();
        public TrialLayoutMarker[] markers = Array.Empty<TrialLayoutMarker>();
        public TrialLayoutRect combatArea = new TrialLayoutRect { x = 2, y = 2, width = 22, height = 13 };
    }

    [Serializable]
    public sealed class TrialTileRect
    {
        public int x;
        public int y;
        public int width = 1;
        public int height = 1;
        public int tileId;
    }

    [Serializable]
    public sealed class TrialLayoutMarker
    {
        public string id = string.Empty;
        public int x;
        public int y;
    }

    [Serializable]
    public sealed class TrialLayoutProp
    {
        // PropDatabase에 등록된 기존 게임 프리팹 ID. 빈 값은 무시한다.
        public string id = string.Empty;
        public float x;
        public float y;
    }

    [Serializable]
    public sealed class TrialLayoutRect
    {
        public int x;
        public int y;
        public int width;
        public int height;
    }

    public static class TrialLayoutRuntime
    {
        // TrialLayout.xml은 게임의 원본 방 XML과 같은 <room> 형식이다.
        // 기존 TrialLayout.json은 이전 에디터 출력을 계속 쓸 수 있도록 보조한다.
        private const string XmlLayoutFileName = "TrialLayout.xml";
        private const string JsonLayoutFileName = "TrialLayout.json";
        private const string PlayerSpawnMarker = "player_spawn";
        private const string ArenaCenterMarker = "arena_center";
        private const string NativeFloorMaterialSourceName = "DeepCave_Anvil";

        private static TrialLayoutData? cachedLayout;
        private static bool triedExternalFile;
        private static bool loadedExternalFile;

        // 기본 AssetBundle 맵을 보존할지, 사용자가 편집기에서 내보낸
        // TrialLayout.xml(또는 호환용 JSON)을 적용할지 결정하는 플래그다.
        public static bool HasExternalLayout
        {
            get
            {
                _ = Current;
                return loadedExternalFile;
            }
        }

        public static Vector3 PlayerSpawnPosition => GetMarkerPosition(Current, PlayerSpawnMarker, 13, 6);

        public static Vector3 ArenaCenterPosition => GetMarkerPosition(Current, ArenaCenterMarker, 13, 9);

        public static Vector2 CombatHalfExtents
        {
            get
            {
                TrialLayoutRect area = Current.combatArea;
                return new Vector2(Mathf.Max(1f, (area.width - 2) * 0.5f), Mathf.Max(1f, (area.height - 2) * 0.5f));
            }
        }

        private static TrialLayoutData Current
        {
            get
            {
                if (cachedLayout == null) cachedLayout = LoadLayout();
                return cachedLayout;
            }
        }

        // 전용 층의 원본 타일맵을 지우고 외부 XML/JSON으로 정의된 기존 게임 타일을 채운다.
        public static bool ApplyTo(FloorGenerator generator)
        {
            return ApplyTo(generator, null);
        }

        // The bundle contains one authoritative source room per trial floor.
        // Apply the XML passed by the floor-allocation callback so battle and
        // reward variants do not all fall back to the base TrialLayout.xml.
        public static bool ApplyTo(FloorGenerator generator, string? layoutXml)
        {
            TrialLayoutData? parsedLayout = null;
            if (!string.IsNullOrWhiteSpace(layoutXml))
            {
                try
                {
                    parsedLayout = LoadXmlLayout(layoutXml);
                }
                catch (Exception exception)
                {
                    Debug.LogError("[Sephiria Endless Trial] 번들 층 XML을 읽지 못했습니다: " + exception.Message);
                    return false;
                }
            }

            TrialLayoutData layout = parsedLayout ?? Current;
            if (!Validate(layout))
            {
                Debug.LogError("[Sephiria Endless Trial] 외부 레이아웃 형식이 올바르지 않습니다.");
                if (parsedLayout != null) return false;
                cachedLayout = CreateDefault();
                layout = cachedLayout;
            }

            if (generator is TileFloorGenerator tiles)
                return ApplyToTileFloorGenerator(generator, tiles, layout);

            // The first published trial bundle was intentionally based on a
            // FullyDesignedFloorGenerator.  It has ordinary child Tilemaps
            // rather than TileFloorGenerator fields, so support it directly
            // instead of silently falling back to the bundle's painted map.
            if (generator is FullyDesignedFloorGenerator designed)
                return ApplyToFullyDesignedFloorGenerator(designed, layout);

            Debug.LogError("[Sephiria Endless Trial] 지원하지 않는 시련 층 생성기입니다: " + generator.GetType().Name);
            return false;
        }

        // Converts editor-grid coordinates to the centred cell coordinates of
        // the existing FullyDesignedFloorGenerator prefab.
        public static Vector3 GetLayoutLocalPosition(FloorGenerator generator, Vector3 layoutPosition)
        {
            if (generator is FullyDesignedFloorGenerator designed)
                return layoutPosition + GetFullyDesignedLayoutOrigin(designed, Current);
            return layoutPosition;
        }

        private static bool ApplyToTileFloorGenerator(FloorGenerator generator, TileFloorGenerator tiles, TrialLayoutData layout)
        {
            // The extracted Unity project contains placeholder shaders with the
            // same names as the game's shaders. Use the real materials already
            // loaded by the game before displaying any tiles from the bundle.
            if (!BindNativeTileMaterials(tiles)) return false;

            // SingleRoomFloorGenerator가 실제로 렌더한 바닥 타일을 먼저 확보한다.
            // -1은 JSON에서 "이 생성기의 검증된 기본 바닥 타일"을 뜻한다.
            TileBase? templateGround = FindTemplateGroundTile(tiles);

            // SingleRoomFloorGenerator creates the source room props before
            // FloorGenerator raises the clientside allocation event. Remove
            // those registered props through the game's own lifecycle method
            // before replacing the source room with the selected XML layout.
            ClearGeneratedProps(generator);
            ClearTilemaps(tiles);
            ResetDungeonBounds(tiles);

            bool success = true;
            success &= FillGround(tiles, layout.ground, upperLayer: false, templateGround);
            success &= FillGround(tiles, layout.upperGround, upperLayer: true);
            success &= FillWall(tiles, layout.wall);
            success &= FillCliff(tiles, layout.cliff);
            if (success) RebuildNativeRoomSurroundings(tiles, layout);
            if (success) SpawnProps(generator, layout.props);

            generator.isSafeFloor = true;
            generator.isCalPlayTimeFloor = false;
            generator.generateMap = false;
            generator.cameraBoundary = false;
            generator.rain = false;
            generator.fog = false;
            generator.fireSpark = false;

            if (tiles.ground != null) tiles.ground.RefreshAllTiles();
            if (tiles.upperGround != null) tiles.upperGround.RefreshAllTiles();
            if (tiles.water != null) tiles.water.RefreshAllTiles();
            if (tiles.wall != null) tiles.wall.RefreshAllTiles();
            if (tiles.roof != null) tiles.roof.RefreshAllTiles();
            if (tiles.cliff != null) tiles.cliff.RefreshAllTiles();
            if (tiles.cliffCollider != null) tiles.cliffCollider.RefreshAllTiles();
            if (tiles.cliffBackground != null) tiles.cliffBackground.RefreshAllTiles();

            if (success)
            {
                TileBase? centerTile = tiles.ground?.GetTile(new Vector3Int(layout.width / 2, layout.height / 2, 0));
                int groundCount = CountTiles(tiles.ground, layout.width, layout.height);
                Debug.Log($"[Sephiria Endless Trial] 레이아웃 적용 완료: {layout.name} ({layout.width}x{layout.height}), 중심 바닥={(centerTile != null ? centerTile.name : "없음")}, 바닥 셀={groundCount}");
#if DEBUG
                if (generator.GetComponent<TrialAirRenderDiagnostics>() == null)
                    generator.gameObject.AddComponent<TrialAirRenderDiagnostics>();
#endif
            }
            return success;
        }

        private static bool BindNativeTileMaterials(TileFloorGenerator trialFloor)
        {
            Dictionary<uint, GameObject>? registeredPrefabs = RaceDatabase.floorGeneratorDictionary;
            if (registeredPrefabs == null)
            {
                Debug.LogError("[Sephiria Endless Trial] 원본 층 등록 목록을 찾지 못해 시련 타일 재질을 교체할 수 없습니다.");
                return false;
            }

            TileFloorGenerator? nativeFloor = registeredPrefabs.Values
                .Where(prefab => prefab != null &&
                    string.Equals(prefab.name, NativeFloorMaterialSourceName, StringComparison.Ordinal))
                .Select(prefab => prefab.GetComponent<TileFloorGenerator>())
                .FirstOrDefault(floor => floor != null);
            if (nativeFloor == null)
            {
                Debug.LogError("[Sephiria Endless Trial] 원본 재질 제공 층을 찾지 못했습니다: " + NativeFloorMaterialSourceName);
                return false;
            }

            TilemapRenderer[] nativeRenderers = nativeFloor.GetComponentsInChildren<TilemapRenderer>(true);
            TilemapRenderer[] trialRenderers = trialFloor.GetComponentsInChildren<TilemapRenderer>(true);
            var replacements = new List<KeyValuePair<TilemapRenderer, Material>>();
            foreach (TilemapRenderer trialRenderer in trialRenderers)
            {
                TilemapRenderer? nativeRenderer = nativeRenderers.FirstOrDefault(renderer =>
                    string.Equals(renderer.gameObject.name, trialRenderer.gameObject.name, StringComparison.Ordinal));
                Material? nativeMaterial = nativeRenderer != null ? nativeRenderer.sharedMaterial : null;
                if (nativeMaterial == null || nativeMaterial.shader == null)
                {
                    if (!trialRenderer.enabled) continue;
                    Debug.LogError("[Sephiria Endless Trial] 원본 타일 재질을 찾지 못했습니다: " + trialRenderer.gameObject.name);
                    return false;
                }
                replacements.Add(new KeyValuePair<TilemapRenderer, Material>(trialRenderer, nativeMaterial));
            }

            TilemapRenderer? groundRenderer = trialFloor.ground != null
                ? trialFloor.ground.GetComponent<TilemapRenderer>() : null;
            if (groundRenderer == null || !replacements.Any(entry => entry.Key == groundRenderer))
            {
                Debug.LogError("[Sephiria Endless Trial] 시련 바닥에 적용할 원본 Ground 재질을 찾지 못했습니다.");
                return false;
            }

            foreach (KeyValuePair<TilemapRenderer, Material> replacement in replacements)
                replacement.Key.sharedMaterial = replacement.Value;

            Material groundMaterial = groundRenderer.sharedMaterial;
            Debug.Log($"[Sephiria Endless Trial] 원본 타일 재질 적용: source={NativeFloorMaterialSourceName}, " +
                $"renderers={replacements.Count}, groundMaterial={groundMaterial.name}#{groundMaterial.GetInstanceID()}, " +
                $"groundShader={groundMaterial.shader.name}#{groundMaterial.shader.GetInstanceID()}");
            return true;
        }

        private static bool ApplyToFullyDesignedFloorGenerator(FullyDesignedFloorGenerator generator, TrialLayoutData layout)
        {
            ConfigureBrightTrialLighting(generator);

            Tilemap? ground = FindTilemap(generator.transform, "Ground");
            Tilemap? wall = FindTilemap(generator.transform, "Wall");
            if (ground == null || wall == null)
            {
                Debug.LogError("[Sephiria Endless Trial] 번들 프리팹에서 Ground 또는 Wall Tilemap을 찾지 못했습니다.");
                return false;
            }

            // FullyDesignedFloorGenerator has no TileFloorGenerator.SetWallTile()
            // helper.  Keep the same four maps used by that helper so an XML
            // wall gets its roof, ground replacement and upper-ground layer.
            // These maps are created at runtime for older bundles that only
            // authored Ground and Wall.
            // The XML importer bakes the layout into the native map names and
            // sorting setup. Reuse those maps at runtime instead of creating
            // parallel TrialLayout_* maps whose inherited sorting order puts the
            // roof in front of airborne actors.
            Tilemap upperGround = GetOrCreateTilemap(generator.transform, "UpperGround", ground, 1);
            Tilemap roof = GetOrCreateTilemap(generator.transform, "Roof", wall, 0);
            Tilemap? cliff = layout.cliff.Length > 0
                ? GetOrCreateTilemap(generator.transform, "Cliff", ground, -1)
                : null;

            TileBase? templateGround = FindFirstTile(ground) ?? TileDatabase.GetGroundTile(5)?.tile;
            foreach (Tilemap tilemap in generator.GetComponentsInChildren<Tilemap>(true))
                tilemap.ClearAllTiles();

            Vector3Int origin = Vector3Int.RoundToInt(GetFullyDesignedLayoutOrigin(generator, layout));
            bool success = true;
            success &= FillGroundMap(ground, layout.ground, origin, templateGround);
            success &= FillGroundMap(upperGround, layout.upperGround, origin, null);
            success &= FillWallMap(wall, roof, ground, upperGround, layout.wall, origin, origin.y + layout.height - 1);
            if (cliff != null) success &= FillCliffMap(cliff, layout.cliff, origin);
            if (success) SpawnProps(generator, layout.props, origin);

            ground.RefreshAllTiles();
            wall.RefreshAllTiles();
            upperGround.RefreshAllTiles();
            roof.RefreshAllTiles();
            cliff?.RefreshAllTiles();

            if (success)
            {
                TileBase? centerTile = ground.GetTile(origin + new Vector3Int(layout.width / 2, layout.height / 2, 0));
                int groundCount = CountTiles(ground, layout.width, layout.height, origin);
                Debug.Log($"[Sephiria Endless Trial] FullyDesigned 번들에 레이아웃 적용 완료: {layout.name} ({layout.width}x{layout.height}), 중심 바닥={(centerTile != null ? centerTile.name : "없음")}, 바닥 셀={groundCount}");
            }
            return success;
        }

        // The first bundle used Deep Cave's night values.  FloorGenerator
        // applies these values to the scene-wide 2D light, which darkens every
        // player and monster as well as the tilemaps.  A trial arena should be
        // readable, so make the runtime floor explicitly use normal daylight.
        private static void ConfigureBrightTrialLighting(FullyDesignedFloorGenerator generator)
        {
            generator.multiplySunlight = true;
            generator.dayLigthColor = Color.white;

            if (GameCamera.Instance?.sunAndMoon?.dayCycle != null)
            {
                GameCamera.Instance.sunAndMoon.dayCycle.UseSunLight = true;
                GameCamera.Instance.sunAndMoon.dayCycle.SetDayLightColor(Color.white);
            }
        }

        private static Vector3 GetFullyDesignedLayoutOrigin(FullyDesignedFloorGenerator generator, TrialLayoutData layout)
        {
            float x = Mathf.Round((generator.bottomLeft.x + generator.topRight.x - layout.width) * 0.5f);
            float y = Mathf.Round((generator.bottomLeft.y + generator.topRight.y - layout.height) * 0.5f);
            return new Vector3(x, y, 0f);
        }

        private static Tilemap? FindTilemap(Transform root, string objectName)
        {
            foreach (Tilemap map in root.GetComponentsInChildren<Tilemap>(true))
            {
                if (string.Equals(map.gameObject.name, objectName, StringComparison.OrdinalIgnoreCase))
                    return map;
            }
            return null;
        }

        private static Tilemap GetOrCreateTilemap(Transform root, string objectName, Tilemap template, int sortingOrderOffset)
        {
            Tilemap? existing = FindTilemap(root, objectName);
            if (existing != null) return existing;

            Grid? grid = root.GetComponentInChildren<Grid>(true);
            Transform parent = grid != null ? grid.transform : root;
            GameObject mapObject = new GameObject(objectName) { layer = template.gameObject.layer };
            mapObject.transform.SetParent(parent, false);
            Tilemap map = mapObject.AddComponent<Tilemap>();
            TilemapRenderer renderer = mapObject.AddComponent<TilemapRenderer>();
            TilemapRenderer? templateRenderer = template.GetComponent<TilemapRenderer>();
            if (templateRenderer != null)
            {
                renderer.sortOrder = templateRenderer.sortOrder;
                renderer.sortingLayerID = templateRenderer.sortingLayerID;
                renderer.sortingOrder = templateRenderer.sortingOrder + sortingOrderOffset;
            }
            return map;
        }

        private static TileBase? FindFirstTile(Tilemap tilemap)
        {
            foreach (Vector3Int position in tilemap.cellBounds.allPositionsWithin)
            {
                TileBase? tile = tilemap.GetTile(position);
                if (tile != null) return tile;
            }
            return null;
        }

        private static bool FillGroundMap(Tilemap target, TrialTileRect[] rects, Vector3Int origin, TileBase? templateGround)
        {
            bool success = true;
            foreach (TrialTileRect rect in rects ?? Array.Empty<TrialTileRect>())
            {
                TileBase? tile = rect.tileId == -1 ? templateGround ?? TileDatabase.GetGroundTile(5)?.tile : TileDatabase.GetGroundTile(rect.tileId)?.tile;
                if (tile == null)
                {
                    Debug.LogError($"[Sephiria Endless Trial] 사용할 수 없는 바닥 타일 ID: {rect.tileId}");
                    success = false;
                    continue;
                }
                for (int x = rect.x; x < rect.x + rect.width; x++)
                    for (int y = rect.y; y < rect.y + rect.height; y++)
                        target.SetTile(origin + new Vector3Int(x, y, 0), tile);
            }
            return success;
        }

        // Mirrors TileFloorGenerator.SetWallTile().  A wall in Sephiria is a
        // compound placement, not merely a sprite on the Wall tilemap:
        // - roof receives the matching roof tile,
        // - ground / upperGround receive the wall entity's override tiles,
        // - a transparent wall is inserted directly above when required so
        //   adjacent Rule Tiles resolve exactly as the game's authored rooms.
        private static bool FillWallMap(
            Tilemap wall,
            Tilemap roof,
            Tilemap ground,
            Tilemap upperGround,
            TrialTileRect[] rects,
            Vector3Int origin,
            int topBoundaryY)
        {
            bool success = true;
            foreach (TrialTileRect rect in rects ?? Array.Empty<TrialTileRect>())
            {
                WallRoofTileEntity? entity = TileDatabase.GetWallTile(rect.tileId);
                if (entity?.wallTile == null)
                {
                    Debug.LogError($"[Sephiria Endless Trial] 존재하지 않는 벽 타일 ID: {rect.tileId}");
                    success = false;
                    continue;
                }
                for (int x = rect.x; x < rect.x + rect.width; x++)
                    for (int y = rect.y; y < rect.y + rect.height; y++)
                        SetFullyDesignedWallTile(wall, roof, ground, upperGround,
                            origin + new Vector3Int(x, y, 0), entity, topBoundaryY);
            }
            return success;
        }

        private static void SetFullyDesignedWallTile(
            Tilemap wall,
            Tilemap roof,
            Tilemap ground,
            Tilemap upperGround,
            Vector3Int position,
            WallRoofTileEntity entity,
            int topBoundaryY)
        {
            wall.SetTile(position, entity.wallTile);
            roof.SetTile(position, entity.roofTile);
            ground.SetTile(position, entity.overwriteGround);
            upperGround.SetTile(position, entity.overwriteUpperGround);

            if (!entity.makeTransparentTileToAbove)
                return;

            // A transparent wall above an interior wall is required for the
            // game's RuleTile connections.  Do not add one beyond the XML
            // room's top edge: there is no authored roof/background there and
            // it renders as the unwanted black strip seen above the arena.
            if (position.y >= topBoundaryY)
                return;

            Vector3Int positionAbove = position + Vector3Int.up;
            if (wall.GetTile(positionAbove) != null)
                return;

            TileBase? transparentWall = TileDatabase.GetWallTile(1000)?.wallTile;
            if (transparentWall != null)
                wall.SetTile(positionAbove, transparentWall);
        }

        private static bool FillCliffMap(Tilemap target, TrialTileRect[] rects, Vector3Int origin)
        {
            bool success = true;
            foreach (TrialTileRect rect in rects ?? Array.Empty<TrialTileRect>())
            {
                CliffTileEntity? entity = TileDatabase.GetCliffTile(rect.tileId);
                if (entity?.tile == null)
                {
                    Debug.LogError($"[Sephiria Endless Trial] 존재하지 않는 절벽 타일 ID: {rect.tileId}");
                    success = false;
                    continue;
                }
                for (int x = rect.x; x < rect.x + rect.width; x++)
                    for (int y = rect.y; y < rect.y + rect.height; y++)
                        target.SetTile(origin + new Vector3Int(x, y, 0), entity.tile);
            }
            return success;
        }

        private static TrialLayoutData LoadLayout()
        {
            if (!triedExternalFile)
            {
                triedExternalFile = true;
                try
                {
                    string directory = Path.GetDirectoryName(typeof(EndlessMod).Assembly.Location) ?? string.Empty;
                    // XML is the preferred format because it is the same room
                    // format the game and the visual map editor use.  This means
                    // an exported file can be opened again in the editor without
                    // a lossy JSON conversion.
                    string xmlPath = Path.Combine(directory, XmlLayoutFileName);
                    if (File.Exists(xmlPath))
                    {
                        TrialLayoutData? loaded = LoadXmlLayout(File.ReadAllText(xmlPath));
                        if (loaded != null && Validate(loaded))
                        {
                            loadedExternalFile = true;
                            Debug.Log($"[Sephiria Endless Trial] XML 레이아웃을 불러왔습니다: {xmlPath}");
                            return loaded;
                        }
                        Debug.LogError($"[Sephiria Endless Trial] {XmlLayoutFileName}의 형식이 올바르지 않습니다. JSON 호환 파일을 확인합니다.");
                    }

                    // Older user-authored layouts remain available while the
                    // editor transition is in progress.  XML always wins when
                    // both files are present and valid.
                    string jsonPath = Path.Combine(directory, JsonLayoutFileName);
                    if (File.Exists(jsonPath))
                    {
                        TrialLayoutData? loaded = UnityEngine.JsonUtility.FromJson<TrialLayoutData>(File.ReadAllText(jsonPath));
                        if (loaded != null && Validate(loaded))
                        {
                            loadedExternalFile = true;
                            Debug.Log($"[Sephiria Endless Trial] 호환 JSON 레이아웃을 불러왔습니다: {jsonPath}");
                            return loaded;
                        }
                        Debug.LogError($"[Sephiria Endless Trial] {JsonLayoutFileName}의 형식이 올바르지 않습니다.");
                    }

                    Debug.LogWarning($"[Sephiria Endless Trial] {XmlLayoutFileName} 또는 {JsonLayoutFileName}을 찾지 못했습니다. 내장 기본 레이아웃을 사용합니다.");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Sephiria Endless Trial] 레이아웃 파일을 읽지 못했습니다: {e.Message}");
                }
            }
            return CreateDefault();
        }

        // Reads the <room> XML emitted by Trial_MapEditor.html.  Grid rows are
        // stored top-to-bottom in XML, while Unity Tilemap cells grow upward, so
        // the row index is intentionally inverted when creating tile records.
        private static TrialLayoutData? LoadXmlLayout(string text)
        {
            XDocument document = XDocument.Parse(text);
            XElement? room = document.Root;
            if (room == null || room.Name.LocalName != "room") return null;

            if (!TryGetIntAttribute(room, "width", out int width) ||
                !TryGetIntAttribute(room, "height", out int height) ||
                width < 5 || height < 5)
            {
                return null;
            }

            XElement? mainLayer = room.Elements().FirstOrDefault(element => element.Name.LocalName == "mainLayer");
            if (mainLayer == null) return null;

            TrialLayoutData layout = new TrialLayoutData
            {
                name = room.Attribute("tag")?.Value ?? "Custom Trial Arena",
                width = width,
                height = height,
                ground = ReadXmlGrid(mainLayer, "ground", width, height),
                upperGround = ReadXmlGrid(mainLayer, "upperGround", width, height),
                wall = ReadXmlGrid(mainLayer, "wall", width, height),
                cliff = ReadXmlGrid(mainLayer, "cliff", width, height),
                props = ReadXmlProps(mainLayer, out TrialLayoutMarker[] markers),
                markers = markers,
                // The outer one-cell border is normally wall/collision and is
                // never a valid combat-spawn location.
                combatArea = new TrialLayoutRect
                {
                    x = 1,
                    y = 1,
                    width = Mathf.Max(3, width - 2),
                    height = Mathf.Max(3, height - 2)
                }
            };

            return layout;
        }

        private static bool TryGetIntAttribute(XElement element, string name, out int value)
        {
            return int.TryParse(element.Attribute(name)?.Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
        }

        private static TrialTileRect[] ReadXmlGrid(XElement mainLayer, string layerName, int width, int height)
        {
            XElement? layer = mainLayer.Elements().FirstOrDefault(element => element.Name.LocalName == layerName);
            if (layer == null) return Array.Empty<TrialTileRect>();

            List<TrialTileRect> tiles = new List<TrialTileRect>();
            string[] lines = (layer.Value ?? string.Empty).Replace("\r", string.Empty)
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            for (int row = 0; row < lines.Length && row < height; row++)
            {
                string[] values = lines[row].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                int y = height - 1 - row;
                for (int x = 0; x < values.Length && x < width; x++)
                {
                    if (!int.TryParse(values[x], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tileId) || tileId < 0)
                        continue;

                    tiles.Add(new TrialTileRect { x = x, y = y, tileId = tileId });
                }
            }

            return tiles.ToArray();
        }

        private static TrialLayoutProp[] ReadXmlProps(XElement mainLayer, out TrialLayoutMarker[] markers)
        {
            List<TrialLayoutProp> props = new List<TrialLayoutProp>();
            List<TrialLayoutMarker> markerList = new List<TrialLayoutMarker>();
            XElement? propsElement = mainLayer.Elements().FirstOrDefault(element => element.Name.LocalName == "props");
            if (propsElement == null)
            {
                markers = Array.Empty<TrialLayoutMarker>();
                return Array.Empty<TrialLayoutProp>();
            }

            foreach (XElement element in propsElement.Elements())
            {
                if (!TryParsePosition(element.Attribute("position")?.Value, out float x, out float y))
                    continue;

                string id = element.Name.LocalName;
                string? markerId = id switch
                {
                    "TrialMarker_PlayerSpawn" => PlayerSpawnMarker,
                    "TrialMarker_ArenaCenter" => ArenaCenterMarker,
                    "TrialMarker_Portal" => "trial_portal",
                    "TrialMarker_SaveReturnPortal" => "save_return_portal",
                    _ => null
                };

                if (markerId != null)
                {
                    markerList.Add(new TrialLayoutMarker
                    {
                        id = markerId,
                        x = Mathf.RoundToInt(x),
                        y = Mathf.RoundToInt(y)
                    });
                    continue;
                }

                // Marker transforms are serialized into each floor prefab by
                // the Unity importer. They are metadata, not PropDatabase
                // entries to instantiate at runtime.
                if (id.StartsWith("TrialMarker_", StringComparison.Ordinal))
                    continue;

                props.Add(new TrialLayoutProp { id = id, x = x, y = y });
            }

            markers = markerList.ToArray();
            return props.ToArray();
        }

        private static bool TryParsePosition(string? value, out float x, out float y)
        {
            x = 0f;
            y = 0f;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string[] values = value.Split(',');
            return values.Length >= 2 &&
                   float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                   float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
        }

        private static bool Validate(TrialLayoutData? layout)
        {
            return layout != null && layout.width >= 5 && layout.height >= 5 &&
                   layout.ground != null && layout.wall != null && layout.markers != null;
        }

        private static void ClearTilemaps(TileFloorGenerator tiles)
        {
            Clear(tiles.ground);
            Clear(tiles.upperGround);
            Clear(tiles.water);
            Clear(tiles.wall);
            Clear(tiles.roof);
            Clear(tiles.cliff);
            Clear(tiles.cliffCollider);
            Clear(tiles.cliffBackground);
        }

        private static void Clear(Tilemap? tilemap)
        {
            if (tilemap != null) tilemap.ClearAllTiles();
        }

        // SingleRoomFloorGenerator paints a 20-cell native outline around its
        // room and fills the cliff background across that same range. Rebuild
        // these engine-owned layers for the XML dimensions instead of keeping
        // the template room's old 26x18 edge art behind the new tile layout.
        private static void RebuildNativeRoomSurroundings(TileFloorGenerator tiles, TrialLayoutData layout)
        {
            const int outlineSize = 20;
            CliffTileEntity? background = TileDatabase.GetCliffTile(1001);
            CliffTileEntity? transparentCliff = TileDatabase.GetCliffTile(1000);
            TileBase? transparentWall = TileDatabase.GetWallTile(1000)?.wallTile;

            for (int x = -outlineSize; x < layout.width + outlineSize; x++)
            {
                for (int y = -outlineSize; y < layout.height + outlineSize; y++)
                {
                    Vector3Int position = new Vector3Int(x, y, 0);
                    bool insideRoom = x >= 0 && x < layout.width && y >= 0 && y < layout.height;
                    if (!insideRoom)
                    {
                        if (tiles.roof != null) tiles.roof.SetTile(position, tiles.defaultOutlineRoofTile);
                        if (tiles.wall != null) tiles.wall.SetTile(position, tiles.defaultOutlineWallTile);
                    }

                    if (tiles.cliffBackground != null && background?.tile != null)
                        tiles.cliffBackground.SetTile(position, background.tile);

                    TileBase? groundTile = tiles.ground?.GetTile(position);
                    TileBase? wallTile = tiles.wall?.GetTile(position);
                    TileBase? cliffTile = tiles.cliff?.GetTile(position);
                    bool wallIsTransparent = wallTile != null && transparentWall != null && wallTile == transparentWall;
                    if (tiles.useTransCliffTile && transparentCliff?.tile != null && groundTile == null &&
                        (wallTile == null || wallIsTransparent) && cliffTile == null)
                    {
                        tiles.SetCliffTile(position, transparentCliff.tile);
                    }
                }
            }
        }

        // TileFloorGenerator는 이전에 생성한 방의 카메라 경계를 누적하므로, 새 전용 맵 크기로 다시 계산한다.
        private static void ResetDungeonBounds(TileFloorGenerator tiles)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(TileFloorGenerator).GetField("leftmostPos", flags)?.SetValue(tiles, 9999f);
            typeof(TileFloorGenerator).GetField("rightmostPos", flags)?.SetValue(tiles, -9999f);
            typeof(TileFloorGenerator).GetField("uppermostPos", flags)?.SetValue(tiles, -9999f);
            typeof(TileFloorGenerator).GetField("lowestPos", flags)?.SetValue(tiles, 9999f);
        }

        private static void ClearGeneratedProps(FloorGenerator generator)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo? allPropsField = typeof(FloorGenerator).GetField("allProps", flags);
            if (allPropsField?.GetValue(generator) is System.Collections.IList props)
            {
                List<GameObject> existing = new List<GameObject>();
                foreach (object? value in props)
                    if (value is GameObject prop && prop != null) existing.Add(prop);

                foreach (GameObject prop in existing)
                {
                    try { generator.DestroyProp(prop); }
                    catch (Exception exception)
                    {
                        Debug.LogWarning("[Sephiria Endless Trial] 원본 방 프롭 정리에 실패했습니다: " + exception.Message);
                    }
                    generator.floorRelatedNetworkObjects.Remove(prop);
                }
                props.Clear();
            }

            ClearGeneratorCollection(generator, "searchableProps");
            ClearGeneratorCollection(generator, "spawnPoints");
            ClearGeneratorCollection(generator, "allEnemySpawners");
            ClearGeneratorCollection(generator, "allBattleZones");
        }

        private static void ClearGeneratorCollection(FloorGenerator generator, string fieldName)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo? field = typeof(FloorGenerator).GetField(fieldName, flags);
            if (field?.GetValue(generator) is System.Collections.IDictionary dictionary)
                dictionary.Clear();
            else if (field?.GetValue(generator) is System.Collections.IList list)
                list.Clear();
        }

        private static TileBase? FindTemplateGroundTile(TileFloorGenerator tiles)
        {
            if (tiles.ground != null)
            {
                foreach (Vector3Int position in tiles.ground.cellBounds.allPositionsWithin)
                {
                    TileBase? tile = tiles.ground.GetTile(position);
                    // RuleTile의 런타임 인스턴스는 TileDatabase의 원본 참조와 다를 수 있다.
                    // 여기서는 DB 역참조 여부와 무관하게 실제 화면에 있던 타일을 사용한다.
                    if (tile != null)
                        return tile;
                }
            }

            return tiles.defaultGroundTile ?? TileDatabase.GetGroundTile(5)?.tile;
        }

        private static int CountTiles(Tilemap? tilemap, int width, int height)
        {
            return CountTiles(tilemap, width, height, Vector3Int.zero);
        }

        private static int CountTiles(Tilemap? tilemap, int width, int height, Vector3Int origin)
        {
            if (tilemap == null) return 0;

            int count = 0;
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (tilemap.GetTile(origin + new Vector3Int(x, y, 0)) != null)
                        count++;
                }
            }
            return count;
        }

        private static bool FillGround(TileFloorGenerator tiles, TrialTileRect[] rects, bool upperLayer, TileBase? templateGround = null)
        {
            bool success = true;
            Tilemap? target = upperLayer ? tiles.upperGround : tiles.ground;
            if (target == null)
            {
                Debug.LogError($"[Sephiria Endless Trial] {(upperLayer ? "UpperGround" : "Ground")} Tilemap을 찾지 못했습니다.");
                return false;
            }

            foreach (TrialTileRect rect in rects ?? Array.Empty<TrialTileRect>())
            {
                TileBase? tile;
                if (!upperLayer && rect.tileId == -1)
                {
                    tile = templateGround ?? TileDatabase.GetGroundTile(5)?.tile;
                }
                else
                {
                    GroundTileEntity? entity = TileDatabase.GetGroundTile(rect.tileId);
                    tile = entity?.tile;
                }

                if (tile == null)
                {
                    Debug.LogError($"[Sephiria Endless Trial] 사용할 수 없는 바닥 타일 ID: {rect.tileId}");
                    success = false;
                    continue;
                }
                for (int x = rect.x; x < rect.x + rect.width; x++)
                {
                    for (int y = rect.y; y < rect.y + rect.height; y++)
                    {
                        Vector3Int position = new Vector3Int(x, y, 0);
                        if (upperLayer) tiles.SetUpperGroundTile(position, tile);
                        else tiles.SetGroundTile(position, tile);
                    }
                }
            }
            return success;
        }

        private static bool FillWall(TileFloorGenerator tiles, TrialTileRect[] rects)
        {
            bool success = true;
            if (tiles.wall == null)
            {
                Debug.LogError("[Sephiria Endless Trial] Wall Tilemap을 찾지 못했습니다.");
                return false;
            }

            foreach (TrialTileRect rect in rects ?? Array.Empty<TrialTileRect>())
            {
                WallRoofTileEntity? entity = TileDatabase.GetWallTile(rect.tileId);
                if (entity == null || entity.wallTile == null)
                {
                    Debug.LogError($"[Sephiria Endless Trial] 존재하지 않는 벽 타일 ID: {rect.tileId}");
                    success = false;
                    continue;
                }
                for (int x = rect.x; x < rect.x + rect.width; x++)
                {
                    for (int y = rect.y; y < rect.y + rect.height; y++)
                    {
                        Vector3Int position = new Vector3Int(x, y, 0);
                        tiles.SetWallTile(position, entity.wallTile);
                    }
                }
            }
            return success;
        }

        private static bool FillCliff(TileFloorGenerator tiles, TrialTileRect[] rects)
        {
            bool success = true;
            foreach (TrialTileRect rect in rects ?? Array.Empty<TrialTileRect>())
            {
                CliffTileEntity? entity = TileDatabase.GetCliffTile(rect.tileId);
                if (entity == null || entity.tile == null)
                {
                    Debug.LogError($"[Sephiria Endless Trial] 존재하지 않는 절벽 타일 ID: {rect.tileId}");
                    success = false;
                    continue;
                }
                for (int x = rect.x; x < rect.x + rect.width; x++)
                {
                    for (int y = rect.y; y < rect.y + rect.height; y++)
                    {
                        tiles.SetCliffTile(new Vector3Int(x, y, 0), entity.tile);
                    }
                }
            }
            return success;
        }

        private static void SpawnProps(FloorGenerator generator, TrialLayoutProp[] props, Vector3 layoutOrigin = default)
        {
            if (!NetworkServer.active || generator == null) return;

            foreach (TrialLayoutProp prop in props ?? Array.Empty<TrialLayoutProp>())
            {
                if (prop == null || string.IsNullOrWhiteSpace(prop.id)) continue;

                PropEntity? entity = PropDatabase.FindPropById(prop.id);
                if (entity?.propPrefab == null)
                {
                    Debug.LogWarning($"[Sephiria Endless Trial] 레이아웃 프롭을 찾지 못했습니다: {prop.id}");
                    continue;
                }

                try
                {
                    Vector3 position = generator.transform.position + layoutOrigin + new Vector3(prop.x, prop.y, 0f);
                    generator.CreateProp(
                        UnityEngine.Random.Range(int.MinValue, int.MaxValue), entity, position,
                        Vector3.one, null!, null!);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[Sephiria Endless Trial] 레이아웃 프롭 생성 실패 ({prop.id}): {exception.Message}");
                }
            }
        }

        private static Vector3 GetMarkerPosition(TrialLayoutData layout, string id, int fallbackX, int fallbackY)
        {
            foreach (TrialLayoutMarker marker in layout.markers ?? Array.Empty<TrialLayoutMarker>())
            {
                if (marker != null && string.Equals(marker.id, id, StringComparison.OrdinalIgnoreCase))
                    return new Vector3(marker.x, marker.y, 0f);
            }
            return new Vector3(fallbackX, fallbackY, 0f);
        }

        private static TrialLayoutData CreateDefault()
        {
            // Grassland_HP 기반 층에서 확실히 렌더링되는 타일을 사용한다.
            // 5=GrasslandGrass, 6=GrasslandDirt, 3=GrasslandWall.
            // 어두운 동굴 타일은 동굴 전용 조명/배경과 함께 쓸 때만 교체한다.
            return new TrialLayoutData
            {
                name = "Deep Cave Trial Arena",
                width = 26,
                height = 18,
                ground = new[]
                {
                    new TrialTileRect { x = 0, y = 0, width = 26, height = 18, tileId = -1 }
                },
                upperGround = Array.Empty<TrialTileRect>(),
                wall = new[]
                {
                    new TrialTileRect { x = 0, y = 0, width = 26, height = 1, tileId = 3 },
                    new TrialTileRect { x = 0, y = 17, width = 26, height = 1, tileId = 3 },
                    new TrialTileRect { x = 0, y = 1, width = 1, height = 16, tileId = 3 },
                    new TrialTileRect { x = 25, y = 1, width = 1, height = 16, tileId = 3 }
                },
                markers = new[]
                {
                    new TrialLayoutMarker { id = "player_spawn", x = 13, y = 4 },
                    new TrialLayoutMarker { id = "arena_center", x = 13, y = 9 },
                    new TrialLayoutMarker { id = "trial_portal", x = 13, y = 14 }
                },
                props = Array.Empty<TrialLayoutProp>(),
                combatArea = new TrialLayoutRect { x = 2, y = 2, width = 22, height = 13 }
            };
        }
    }

#if DEBUG
    // Logs one visible grounded and one visible airborne actor in this room.
    // This does not change any renderer or camera setting.
    public sealed class TrialAirRenderDiagnostics : MonoBehaviour
    {
        private TileFloorGenerator? floor;
        private bool loggedGround;
        private bool loggedAir;
        private float nextSampleTime;

        private void Awake()
        {
            floor = GetComponent<TileFloorGenerator>();
        }

        private void Update()
        {
            if (floor == null || floor.ground == null || (loggedGround && loggedAir) ||
                Time.unscaledTime < nextSampleTime)
                return;

            nextSampleTime = Time.unscaledTime + 0.25f;
            Camera? camera = GameCamera.Instance != null ? GameCamera.Instance.Camera : null;
            if (camera == null) return;

            foreach (TopdownRigidbody actor in UnityEngine.Object.FindObjectsByType<TopdownRigidbody>(FindObjectsSortMode.None))
            {
                TopdownActorRenderingMetadata? visual = actor.TopdownActor;
                if (visual == null || visual.body == null) continue;

                Vector3Int cell = floor.ground.WorldToCell(actor.transform.position);
                if (floor.ground.GetTile(cell) == null) continue;

                Vector3 viewport = camera.WorldToViewportPoint(visual.body.position);
                if (viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
                    continue;

                bool airborne = visual.YPos > 0.75f;
                if (airborne ? loggedAir : loggedGround) continue;
                if (airborne) loggedAir = true;
                else loggedGround = true;

                string state = airborne ? "air" : "ground";
                string renderers = string.Join("; ", visual.body.GetComponentsInChildren<Renderer>(true)
                    .Take(4)
                    .Select(renderer =>
                    {
                        SpriteRenderer? sprite = renderer as SpriteRenderer;
                        return $"{renderer.name}:enabled={renderer.enabled},visible={renderer.isVisible},active={renderer.gameObject.activeInHierarchy},layer={renderer.gameObject.layer},sorting={renderer.sortingLayerName}/{renderer.sortingOrder},alpha={(sprite != null ? sprite.color.a : -1f):F2},shader={renderer.sharedMaterial?.shader?.name}";
                    }));
                SortingGroup? group = visual.bodyWrapper;
                string groupSorting = group != null ? $"{group.sortingLayerName}/{group.sortingOrder}" : "none";
                TilemapRenderer? groundRenderer = floor.ground.GetComponent<TilemapRenderer>();
                TilemapRenderer? wallRenderer = floor.wall != null ? floor.wall.GetComponent<TilemapRenderer>() : null;
                TilemapRenderer? roofRenderer = floor.roof != null ? floor.roof.GetComponent<TilemapRenderer>() : null;
                Vector3Int bodyCell = floor.ground.WorldToCell(visual.body.position);
                TileBase? bodyWall = floor.wall != null ? floor.wall.GetTile(bodyCell) : null;
                TileBase? bodyRoof = floor.roof != null ? floor.roof.GetTile(bodyCell) : null;
                Debug.Log($"[시련 렌더 진단] {state} actor={actor.name},rootLayer={actor.gameObject.layer},rootZ={actor.transform.position.z:F2},bodyY={visual.YPos:F2},bodyZ={visual.body.position.z:F2},viewport={viewport},group={groupSorting},renderers=[{renderers}],cameraMask={camera.cullingMask},sortMode={GraphicsSettings.transparencySortMode},sortAxis={GraphicsSettings.transparencySortAxis},ground={Describe(groundRenderer)},wall={Describe(wallRenderer)},roof={Describe(roofRenderer)},bodyWallTile={bodyWall?.name ?? "none"},bodyRoofTile={bodyRoof?.name ?? "none"}");
                if (loggedGround && loggedAir) enabled = false;
                break;
            }
        }

        private static string Describe(TilemapRenderer? renderer)
        {
            return renderer == null
                ? "none"
                : $"{renderer.sortingLayerName}/{renderer.sortingOrder},mode={renderer.mode},enabled={renderer.enabled},shader={renderer.sharedMaterial?.shader?.name}";
        }
    }
#endif
}
