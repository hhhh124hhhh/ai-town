using System;

/// <summary>
/// 建筑 JSON 数据结构（与 Python 端 /api/generate_json 输出格式对齐）。
/// 坐标单位：1 方块 = 1 米，pos 为方块中心，Y 轴向上，与 Luanti 直接对应。
/// </summary>
[Serializable]
public class BuildingData
{
    public string name;
    public BlockData[] blocks;
}

[Serializable]
public class BlockData
{
    public string shape;
    public float[] pos;
    public float[] size;
    public string color;
}
