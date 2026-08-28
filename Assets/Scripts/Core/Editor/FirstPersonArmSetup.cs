using System;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 第一人称手臂（HeldItemUmbrella）前置设置：创建专用渲染层 FirstPersonArm。
/// 该层只被 ArmCamera（URP Overlay）渲染、主相机剔除，实现手臂不穿墙不挡场景。
/// 幂等，可重复执行。
/// </summary>
public static class FirstPersonArmSetup
{
    private const string LayerName = "FirstPersonArm";

    [MenuItem("Tools/AI Town/Setup First Person Arm")]
    public static void SetupLayer()
    {
        int existing = LayerMask.NameToLayer(LayerName);
        if (existing >= 0)
        {
            Debug.Log($"[FpArm] Layer「{LayerName}」已存在（index {existing}），无需重复创建");
            return;
        }

        var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (assets == null || assets.Length == 0)
        {
            throw new InvalidOperationException("TagManager.asset 读取失败");
        }

        var so = new SerializedObject(assets[0]);
        var layers = so.FindProperty("layers");
        if (layers == null || !layers.isArray)
        {
            throw new InvalidOperationException("TagManager.layers 属性缺失");
        }

        int assigned = -1;
        for (int i = 8; i < layers.arraySize; i++)
        {
            var slot = layers.GetArrayElementAtIndex(i);
            if (string.IsNullOrEmpty(slot.stringValue))
            {
                slot.stringValue = LayerName;
                assigned = i;
                break;
            }
        }
        if (assigned < 0)
        {
            throw new InvalidOperationException("Layer 槽位已满（8-31 无空闲）");
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        Debug.Log($"[FpArm] Layer「{LayerName}」已创建于 index {assigned}（注意：原计划的 Layer 8 已被 LookInteractor 占用）");
    }
}
