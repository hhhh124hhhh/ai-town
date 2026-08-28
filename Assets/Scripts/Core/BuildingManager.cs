using System.Collections;
using UnityEngine;

/// <summary>
/// 建筑管理器：管理场景中所有建筑。每栋建筑挂在自己的子根下，互不干扰。
/// 由场景搭建脚本自动创建（挂在 _Buildings 根节点），也可在任意物体上手动添加。
/// </summary>
public class BuildingManager : MonoBehaviour
{
    [Tooltip("分帧生成时每帧最多生成的方块数，防止大批量建筑卡帧")]
    public int blocksPerFrame = 200;

    [Tooltip("启动时自动从 StreamingAssets/Buildings/ 加载的建筑名列表")]
    public string[] autoLoadBuildingNames;

    private Transform _root;

    public static BuildingManager Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        if (autoLoadBuildingNames != null)
        {
            foreach (string name in autoLoadBuildingNames)
            {
                LoadFromFile(name);
            }
        }
    }

    /// <summary>生成一栋建筑，返回该建筑的根节点。</summary>
    public GameObject GenerateFromJson(BuildingData data)
    {
        if (data == null || data.blocks == null || data.blocks.Length == 0)
        {
            Debug.LogWarning("[BuildingManager] 空的或无效的建筑数据");
            return null;
        }

        string rootName = string.IsNullOrEmpty(data.name) ? "Building" : data.name;
        Transform buildingRoot = new GameObject(rootName).transform;
        buildingRoot.SetParent(Root, false);

        foreach (BlockData block in data.blocks)
        {
            CreateBlock(block, buildingRoot);
        }

        Debug.Log($"[BuildingManager] 已生成建筑「{rootName}」，共 {data.blocks.Length} 个方块");
        return buildingRoot.gameObject;
    }

    /// <summary>协程版生成：方块较多时分帧构建，避免单帧卡顿。</summary>
    public IEnumerator GenerateFromJsonCoRoutine(BuildingData data)
    {
        if (data == null || data.blocks == null || data.blocks.Length == 0)
        {
            Debug.LogWarning("[BuildingManager] 空的或无效的建筑数据");
            yield break;
        }

        string rootName = string.IsNullOrEmpty(data.name) ? "Building" : data.name;
        Transform buildingRoot = new GameObject(rootName).transform;
        buildingRoot.SetParent(Root, false);

        int count = 0;
        foreach (BlockData block in data.blocks)
        {
            CreateBlock(block, buildingRoot);
            if (++count % blocksPerFrame == 0)
            {
                yield return null;
            }
        }
        Debug.Log($"[BuildingManager] 已生成建筑「{rootName}」，共 {data.blocks.Length} 个方块");
    }

    /// <summary>从 StreamingAssets/Buildings/ 加载并生成一栋建筑。</summary>
    public GameObject LoadFromFile(string buildingName)
    {
        BuildingData data = JsonLoader.LoadFromFile(buildingName);
        return GenerateFromJson(data);
    }

    /// <summary>按名字清除某栋建筑；不传名则清空全部。</summary>
    public void Clear(string buildingName = null)
    {
        EnsureRoot();
        for (int i = Root.childCount - 1; i >= 0; i--)
        {
            Transform child = Root.GetChild(i);
            if (buildingName == null || child.name == buildingName)
            {
                Destroy(child.gameObject);
            }
        }
    }

    private void CreateBlock(BlockData block, Transform parent)
    {
        // hex 入口：走分类贴图材质库（石/木/砖/砂+玻璃/发光）
        GameObject obj = ShapeFactory.Create(
            block.shape,
            ToVector(block.pos),
            ToVector(block.size),
            block.color);
        obj.transform.SetParent(parent, false);
    }

    private static Vector3 ToVector(float[] v)
    {
        return new Vector3(v[0], v[1], v[2]);
    }

    private Transform Root
    {
        get
        {
            EnsureRoot();
            return _root;
        }
    }

    private void EnsureRoot()
    {
        if (_root == null)
        {
            _root = transform.Find("_Buildings");
            if (_root == null)
            {
                // 场景搭建脚本通常已建好同名的根节点；这里直接复用自身节点
                _root = transform;
            }
        }
    }
}
