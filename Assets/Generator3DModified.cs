using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = System.Random;
using Graphs;  // Make sure you have your Delaunay / Prim MST code in a "Graphs" namespace

public class DungeonGenerator3D : MonoBehaviour
{
    // ---------------------------------------------------
    // ENUM: Types of cells in the dungeon grid
    // ---------------------------------------------------
    private enum CellType
    {
        None,
        MainRoom,
        SideRoom,
        Hallway,
        Stairs
    }

    // ---------------------------------------------------
    // CLASS: Room container
    // ---------------------------------------------------
    private class Room
    {
        public BoundsInt bounds;
        public GameObject roomPrefab;

        public Room(Vector3Int location, Vector3Int size, GameObject prefab)
        {
            bounds = new BoundsInt(location, size);
            roomPrefab = prefab;
        }

        // Helper: checks bounding box intersection
        public static bool Intersect(Room a, Room b)
        {
            return !(
                (a.bounds.xMax <= b.bounds.xMin) ||
                (a.bounds.xMin >= b.bounds.xMax) ||
                (a.bounds.yMax <= b.bounds.yMin) ||
                (a.bounds.yMin >= b.bounds.yMax) ||
                (a.bounds.zMax <= b.bounds.zMin) ||
                (a.bounds.zMin >= b.bounds.zMax)
            );
        }
    }

    // ---------------------------------------------------
    // SERIALIZED FIELDS (Inspector)
    // ---------------------------------------------------
    [Header("Grid Settings")]
    [SerializeField] private Vector3Int size = new Vector3Int(50, 1, 50);
    [SerializeField] private int randomSeed = 0;

    [Header("Main Room")]
    [SerializeField] private GameObject mainRoomPrefab;

    [Header("Side Rooms")]
    [SerializeField] private List<GameObject> sideRoomPrefabs;
    [SerializeField, Range(0f, 1f)] private float sideRoomSpawnChance = 0.5f;
    [SerializeField] private int sideRoomMaxInstances = 5;

    [Header("Hallways")]
    [Tooltip("Prefabs for hallway segments (straight, curved, intersections, etc.).")]
    [SerializeField] private List<GameObject> hallwayPrefabs;

    [Header("Optional Debug Materials")]
    [SerializeField] private Material mainRoomMaterial;
    [SerializeField] private Material sideRoomMaterial;
    [SerializeField] private Material hallwayMaterial;
    [SerializeField] private Material stairsMaterial;

    [Header("Optional Debug Cube Prefab")]
    [Tooltip("If assigned, the script can place small cubes for visual debugging.")]
    [SerializeField] private GameObject cubePrefab;

    // ---------------------------------------------------
    // PRIVATE FIELDS
    // ---------------------------------------------------
    private Random random;
    private Grid3D<CellType> grid;
    private List<Room> rooms;
    private Delaunay3D delaunay;
    private HashSet<Prim.Edge> selectedEdges;

    // ---------------------------------------------------
    // MONOBEHAVIOUR ENTRY
    // ---------------------------------------------------
    private void Start()
    {
        // Initialize
        random = new Random(randomSeed);
        grid = new Grid3D<CellType>(size, Vector3Int.zero);
        rooms = new List<Room>();

        // 1. Place single large main room
        PlaceMainRoom();

        // 2. Place side rooms (multiple, random chance, limited count)
        PlaceSideRooms();

        // 3. Build Delaunay from room centers
        Triangulate();

        // 4. Create MST-based connections (select edges)
        CreateHallways();

        // 5. Pathfind along selected edges & place hallway prefabs
        PathfindHallways();
    }

    // ---------------------------------------------------
    // STEP 1: Place Main Room
    // ---------------------------------------------------
    private void PlaceMainRoom()
    {
        if (!mainRoomPrefab)
        {
            Debug.LogWarning("Main room prefab not assigned! Skipping main room placement.");
            return;
        }

        // Measure prefab size
        Bounds prefabBounds = GetPrefabBounds(mainRoomPrefab);
        Vector3Int mainRoomSize = new Vector3Int(
            Mathf.RoundToInt(prefabBounds.size.x),
            Mathf.RoundToInt(prefabBounds.size.y),
            Mathf.RoundToInt(prefabBounds.size.z)
        );

        // Attempt to place it near the center
        Vector3Int half = new Vector3Int(mainRoomSize.x / 2, 0, mainRoomSize.z / 2);
        Vector3Int location = new Vector3Int(size.x / 2, 0, size.z / 2) - half;

        Room mainRoom = new Room(location, mainRoomSize, mainRoomPrefab);

        // Check if it fits inside the grid
        if (!IsRoomInBounds(mainRoom.bounds))
        {
            Debug.LogError("Main room cannot fit in the given grid dimensions.");
            return;
        }

        // Mark cells
        foreach (Vector3Int pos in mainRoom.bounds.allPositionsWithin)
        {
            if (grid.InBounds(pos))
            {
                grid[pos] = CellType.MainRoom;
            }
        }

        rooms.Add(mainRoom);
        PlaceRoomPrefab(mainRoom, CellType.MainRoom);
    }

    // ---------------------------------------------------
    // STEP 2: Place Side Rooms
    // ---------------------------------------------------
    private void PlaceSideRooms()
    {
        if (sideRoomPrefabs == null || sideRoomPrefabs.Count == 0)
        {
            Debug.LogWarning("No side room prefabs assigned, skipping side rooms.");
            return;
        }

        int placed = 0;
        for (int i = 0; i < sideRoomMaxInstances; i++)
        {
            // Check spawn chance
            if (random.NextDouble() > sideRoomSpawnChance)
                continue;

            // Pick a random side-room prefab
            GameObject prefab = sideRoomPrefabs[random.Next(sideRoomPrefabs.Count)];
            if (!prefab) continue;

            // Measure bounding box
            Bounds b = GetPrefabBounds(prefab);
            Vector3Int sideSize = new Vector3Int(
                Mathf.RoundToInt(b.size.x),
                Mathf.RoundToInt(b.size.y),
                Mathf.RoundToInt(b.size.z)
            );

            // Ensure we have enough space in the grid
            int maxX = size.x - sideSize.x;
            int maxZ = size.z - sideSize.z;
            if (maxX < 0 || maxZ < 0)
                continue; // The side prefab is bigger than the entire grid, skip

            // Random location
            Vector3Int loc = new Vector3Int(
                random.Next(0, maxX + 1),
                0,
                random.Next(0, maxZ + 1)
            );

            Room sideRoom = new Room(loc, sideSize, prefab);

            // Optional "buffer" to prevent overlap
            Room buffer = new Room(
                loc + new Vector3Int(-1, 0, -1),
                sideSize + new Vector3Int(2, 0, 2),
                prefab
            );

            // Check collisions with existing rooms
            bool canPlace = true;
            foreach (var r in rooms)
            {
                if (Room.Intersect(r, buffer))
                {
                    canPlace = false;
                    break;
                }
            }

            // Also check if it is in the grid
            if (!IsRoomInBounds(sideRoom.bounds))
            {
                canPlace = false;
            }

            if (canPlace)
            {
                // Mark
                foreach (var pos in sideRoom.bounds.allPositionsWithin)
                {
                    if (grid.InBounds(pos))
                    {
                        grid[pos] = CellType.SideRoom;
                    }
                }

                rooms.Add(sideRoom);
                PlaceRoomPrefab(sideRoom, CellType.SideRoom);
                placed++;
            }
        }

        Debug.Log($"Placed {placed} side room(s).");
    }

    // ---------------------------------------------------
    // STEP 3: Triangulate (Delaunay)
    // ---------------------------------------------------
    private void Triangulate()
    {
        List<Vertex> vertices = new List<Vertex>();
        foreach (Room r in rooms)
        {
            // Center of each room bounding box
            Vector3 center = (Vector3)r.bounds.position + (Vector3)r.bounds.size / 2f;
            vertices.Add(new Vertex<Room>(center, r));
        }

        delaunay = Delaunay3D.Triangulate(vertices);
    }

    // ---------------------------------------------------
    // STEP 4: Create Hallways via MST
    // ---------------------------------------------------
    private void CreateHallways()
    {
        if (delaunay == null || delaunay.Edges == null || delaunay.Edges.Count == 0)
        {
            Debug.LogWarning("No edges in the Delaunay triangulation. Cannot create hallways.");
            return;
        }

        // Convert edges for MST
        List<Prim.Edge> edges = new List<Prim.Edge>();
        foreach (var e in delaunay.Edges)
        {
            edges.Add(new Prim.Edge(e.U, e.V));
        }

        // Minimum spanning tree
        selectedEdges = new HashSet<Prim.Edge>();
        if (edges.Count > 0)
        {
            var mst = Prim.MinimumSpanningTree(edges, edges[0].U);
            selectedEdges = new HashSet<Prim.Edge>(mst);

            // Optionally add some random edges for variety
            var leftover = new HashSet<Prim.Edge>(edges);
            leftover.ExceptWith(selectedEdges);
            foreach (var edge in leftover)
            {
                if (random.NextDouble() < 0.2f)
                {
                    selectedEdges.Add(edge);
                }
            }
        }
    }

    // ---------------------------------------------------
    // STEP 5: Pathfinding & Place Hallways
    // ---------------------------------------------------
    private void PathfindHallways()
    {
        if (selectedEdges == null || selectedEdges.Count == 0)
        {
            Debug.LogWarning("No edges to pathfind hallways.");
            return;
        }

        // A* or custom pathfinder
        DungeonPathfinder3D aStar = new DungeonPathfinder3D(size);

        foreach (var edge in selectedEdges)
        {
            var roomA = (edge.U as Vertex<Room>).Item;
            var roomB = (edge.V as Vertex<Room>).Item;

            // Convert centers to int positions
            Vector3 centerA = roomA.bounds.center;
            Vector3 centerB = roomB.bounds.center;
            Vector3Int startPos = new Vector3Int(
                Mathf.RoundToInt(centerA.x),
                Mathf.RoundToInt(centerA.y),
                Mathf.RoundToInt(centerA.z)
            );
            Vector3Int endPos = new Vector3Int(
                Mathf.RoundToInt(centerB.x),
                Mathf.RoundToInt(centerB.y),
                Mathf.RoundToInt(centerB.z)
            );

            // Safety check
            if (!grid.InBounds(startPos) || !grid.InBounds(endPos))
                continue;

            // Define cost function for pathfinding
            var path = aStar.FindPath(startPos, endPos, (DungeonPathfinder3D.Node a, DungeonPathfinder3D.Node b) =>
            {
                var cost = new DungeonPathfinder3D.PathCost();
                Vector3Int p = b.Position;

                // If out of bounds, it's not traversable
                if (!grid.InBounds(p))
                {
                    cost.traversable = false;
                    return cost;
                }

                // Basic logic: prefer empty (None) or Hallway
                cost.cost = Vector3Int.Distance(p, endPos);
                switch (grid[p])
                {
                    case CellType.MainRoom:
                    case CellType.SideRoom:
                        cost.cost += 5;
                        break;
                    case CellType.None:
                        cost.cost += 1;
                        break;
                    // Hallway, Stairs, etc. can be quite cheap
                    default:
                        cost.cost += 1;
                        break;
                }

                cost.traversable = true;
                return cost;
            });

            // If path is found, mark & place hallway prefabs
            if (path != null)
            {
                for (int i = 0; i < path.Count; i++)
                {
                    Vector3Int cell = path[i];
                    // Mark if empty
                    if (grid[cell] == CellType.None)
                    {
                        grid[cell] = CellType.Hallway;
                    }
                }

                // Place the hallway segments
                foreach (var cell in path)
                {
                    if (grid[cell] == CellType.Hallway)
                    {
                        // Option A: place a small debug cube
                        // PlaceHallwayCube(cell);

                        // Option B: place a random hallway prefab
                        if (hallwayPrefabs != null && hallwayPrefabs.Count > 0)
                        {
                            GameObject chosen = hallwayPrefabs[random.Next(hallwayPrefabs.Count)];
                            PlaceHallwayPrefab(chosen, cell);
                        }
                    }
                }
            }
        }
    }

    // ---------------------------------------------------
    // HELPER: Room in bounds check
    // ---------------------------------------------------
    private bool IsRoomInBounds(BoundsInt b)
    {
        Vector3Int min = b.position;
        Vector3Int max = b.position + b.size - Vector3Int.one;
        if (min.x < 0 || min.y < 0 || min.z < 0) return false;
        if (max.x >= size.x || max.y >= size.y || max.z >= size.z) return false;
        return true;
    }

    // ---------------------------------------------------
    // HELPER: Measure prefab bounds
    // ---------------------------------------------------
    private Bounds GetPrefabBounds(GameObject prefab)
    {
        if (!prefab) return new Bounds();
        GameObject temp = Instantiate(prefab);
        temp.hideFlags = HideFlags.HideAndDontSave;

        Renderer[] renderers = temp.GetComponentsInChildren<Renderer>();
        if (renderers == null || renderers.Length == 0)
        {
            DestroyImmediate(temp);
            return new Bounds(Vector3.zero, Vector3.zero);
        }

        Bounds combined = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            combined.Encapsulate(renderers[i].bounds);
        }
        DestroyImmediate(temp);

        return combined;
    }

    // ---------------------------------------------------
    // HELPER: Instantiate the room prefab
    // ---------------------------------------------------
    private void PlaceRoomPrefab(Room room, CellType type)
    {
        Vector3 pos = room.bounds.position;
        GameObject go = Instantiate(room.roomPrefab, pos, Quaternion.identity);

        // Debug materials
        if (type == CellType.MainRoom && mainRoomMaterial)
        {
            AssignMaterialToChildren(go, mainRoomMaterial);
        }
        else if (type == CellType.SideRoom && sideRoomMaterial)
        {
            AssignMaterialToChildren(go, sideRoomMaterial);
        }
    }

    // ---------------------------------------------------
    // HELPER: Instantiate a hallway prefab
    // ---------------------------------------------------
    private void PlaceHallwayPrefab(GameObject hallwayPrefab, Vector3Int position)
    {
        if (!hallwayPrefab) return;
        GameObject go = Instantiate(hallwayPrefab, position, Quaternion.identity);
        if (hallwayMaterial)
        {
            AssignMaterialToChildren(go, hallwayMaterial);
        }
    }

    // ---------------------------------------------------
    // DEBUG: Place a small cube for hallways
    // ---------------------------------------------------
    private void PlaceHallwayCube(Vector3Int position)
    {
        if (!cubePrefab) return;
        var go = Instantiate(cubePrefab, position, Quaternion.identity);
        if (hallwayMaterial)
        {
            go.GetComponent<Renderer>().material = hallwayMaterial;
        }
    }

    // ---------------------------------------------------
    // Assign material to all child renderers
    // ---------------------------------------------------
    private void AssignMaterialToChildren(GameObject obj, Material mat)
    {
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
        foreach (var r in renderers)
        {
            r.material = mat;
        }
    }
}
