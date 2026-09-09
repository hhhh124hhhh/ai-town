using System.Collections;
using UnityEngine;
using System.Linq;

/// <summary>
/// NPC 自主游走：围绕出生点（home）在半径内慢速踱步，营造小镇烟火气。
/// 无 NavMesh 依赖——小镇平地，随机锚点 + 直线插值 + 每步向下 Raycast 贴地
/// （同 PlayerGroundShadow 手法）。交互优先级最高：玩家进入交互范围或对话中
/// 立即停步；开场演出（CinematicIntro）期间整体冻结。
/// </summary>
[RequireComponent(typeof(NPCController))]
public class NpcWander : MonoBehaviour
{
    [Header("游走范围")]
    [Tooltip("离出生点最大距离（米）")]
    public float radius = 6f;
    [Tooltip("移动速度（米/秒），与走路动画 5s 循环步幅匹配（0.24 自然 + 提速动画后约 0.6）")]
    public float walkSpeed = 0.6f;

    [Header("节奏")]
    [Tooltip("每次站桩时长区间（秒）")]
    public Vector2 idleTime = new Vector2(2f, 5f);
    [Tooltip("单段路超时（秒），到点强制换目标防卡死")]
    public float legTimeout = 8f;

    [Header("贴地")]
    [Tooltip("向下 Raycast 层掩码（默认 Everything，起点抬高避开自身）")]
    public LayerMask groundMask = ~0;

    private NPCController _npc;
    private Animator _animator; // 绑骨动画（walk 联动，无则跳过）
    private Vector3 _home;
    private Vector3 _target;
    private float _stateUntil;
    private bool _walking;
    private const float ArriveDist = 0.35f;
    private const float GroundRayLift = 5f;   // 起点抬高（越过模型顶+建筑低檐）再向下打

    private void Awake()
    {
        _npc = GetComponent<NPCController>();
        _animator = GetComponentInChildren<Animator>(true);
        _home = transform.position;
    }

    private void OnEnable() => PickIdle();
    private void OnDisable() => SetMotionEnabled(true);

    private void Update()
    {
        // 全局门控：演出期一律冻结（与 NPCController/PlayerBounds 同规则）
        if (CinematicIntro.IsCinematic) return;

        // 交互让位：玩家进入交互范围 / 对话中 → 停步（转向交给 NPCController 的面向逻辑）
        if (_npc.PlayerNearby || _npc.InConversation)
        {
            if (_walking) _walking = false;
            return;
        }

        // 动画联动：行走播 walk（controller 里唯一 clip），停下即停播回默认站姿。
        // enabled=false 时 SkinnedMesh 冻结在当前帧——改为走时 enabled、停时归零 pose 重置。
        // 动画联动：行走播 walk（controller 里唯一 clip），停下即停播回默认站姿。
        // SMPLH walk 循环 5s（自然速度 0.24m/s），动画提速 2.5x 匹配 0.6m/s 移动，
        // 腿步与位移同步，消除"滑步"感。
        if (_animator != null)
        {
            _animator.enabled = _walking;
            if (_walking) _animator.speed = 2.5f;
        }

        if (_walking)
        {
            StepTowards(_target);
            if (Time.unscaledTime >= _stateUntil || Reached(_target))
                PickIdle();
        }
        else if (Time.unscaledTime >= _stateUntil)
        {
            PickNewTarget();
        }
    }

    private void PickIdle()
    {
        _walking = false;
        _stateUntil = Time.unscaledTime + Random.Range(idleTime.x, idleTime.y);
    }

    private void PickNewTarget()
    {
        for (int i = 0; i < 6; i++) // 多试几次避开太贴身的点
        {
            float a = Random.value * Mathf.PI * 2f;
            float r = Mathf.Sqrt(Random.value) * radius; // 面积均匀分布
            var p = _home + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            // 目标离玩家太近会显得"贴脸游走"，重新抽
            if (_npc.PlayerTransform != null &&
                (p - _npc.PlayerTransform.position).sqrMagnitude < 4f) continue;
            // 目标必须落在可行走地面（非建筑顶）：从高处探测，命中 _Buildings 即换点
            if (!Walkable(p)) continue;
            _target = p;
            break;
        }
        _walking = true;
        _stateUntil = Time.unscaledTime + legTimeout;
    }

    /// <summary>该点是否可站立：正上方探测，只认白名单地面根（Ground/_Roads/_River）。</summary>
    private bool Walkable(Vector3 p)
    {
        var hits = Physics.RaycastAll(p + Vector3.up * 5f, Vector3.down, 6.5f, groundMask,
                QueryTriggerInteraction.Ignore);
        return hits.Any(h => GroundRoots.Contains(h.collider.transform.root.name));
    }

    private bool Reached(Vector3 p)
    {
        Vector3 d = transform.position - p; d.y = 0f;
        return d.sqrMagnitude <= ArriveDist * ArriveDist;
    }

    private void StepTowards(Vector3 target)
    {
        Vector3 to = target - transform.position; to.y = 0f;
        float dist = to.magnitude;
        if (dist < 0.001f) return;
        Vector3 dir = to / dist;

        // 前方避让：其他 NPC / 玩家挡道 → 本帧不走（下次 Update 再试，超时会换目标）
        if (BlockedAhead(dir)) return;

        Vector3 pos = transform.position + dir * (walkSpeed * Time.deltaTime);

        // 穿墙拦截：下一步位置若在建筑/道具体积内 → 本帧不走并立刻换目标（防卡脸蹭墙）
        if (WouldClipBuilding(pos))
        {
            PickNewTarget();
            return;
        }

        pos.y = GroundY(pos);
        transform.position = pos;

        // 行走朝向：面向移动方向（NPCController 的 SmoothDamp 只在玩家附近才接管，
        // 远离时这里用直接欧拉角即可，转角平缓无跳变）
        float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        var e = transform.eulerAngles;
        transform.rotation = Quaternion.Euler(0f, Mathf.MoveTowardsAngle(e.y, yaw, 120f * Time.deltaTime), 0f);
    }

    /// <summary>下一步位置是否会撞进建筑/道具/他人体积（胶囊体积用 Overlap 球探测）。
    /// 返回 true 时调用方应放弃当前目标。白名单地面（Ground/_Roads/_River）不在此列。</summary>
    private bool WouldClipBuilding(Vector3 nextPos)
    {
        const float probeR = 0.45f; // 约 NPC 半身宽
        var hits = Physics.OverlapSphere(nextPos, probeR, groundMask, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            var rootName = h.transform.root.name;
            // 自己人（自己/自家骑手）不算
            if (h.transform.root == transform.root) continue;
            // 真地面/路面不算
            if (GroundRoots.Contains(rootName)) continue;
            // 玩家不算（避让已单独处理）
            if (rootName == "Player") continue;
            // 其它 NPC 也别撞（但只阻挡，不换目标——用轻处理：跳过本帧）
            if (h.transform.root.name.StartsWith("NPC_") || rootName.Contains("机器人") || h is CapsuleCollider && h.GetComponent<NPCController>() != null)
                continue;
            // 剩下的就是建筑/道具 → 撞上了
            return true;
        }
        return false;
    }

    private bool BlockedAhead(Vector3 dir)
    {
        // 与玩家双向避让：玩家不在交互范围却正好在下一步位置上（罕见），也停
        var pt = _npc.PlayerTransform;
        if (pt != null)
        {
            Vector3 toP = pt.position - transform.position; toP.y = 0f;
            if (toP.magnitude < 1.2f && Vector3.Dot(toP.normalized, dir) > 0.5f) return true;
        }
        return false;
    }

    /// <summary>可站立判定的地面根名白名单（命中这些才算"真地面"）。</summary>
    private static readonly System.Collections.Generic.HashSet<string> GroundRoots =
        new System.Collections.Generic.HashSet<string> { "Ground", "_Roads", "_River", "Ground_Road" };

    /// <summary>向下 Raycast 求地面高度。只认白名单地面（Ground/_Roads/_River），
    /// 命中其它任何东西（自己/建筑/道具/别的 NPC）都保持当前 y，配合 Walkable 换目标。</summary>
    private float GroundY(Vector3 at)
    {
        Vector3 origin = at + Vector3.up * GroundRayLift;
        if (Physics.Raycast(origin, Vector3.down, out var hit, GroundRayLift + 2f, groundMask,
                QueryTriggerInteraction.Ignore))
        {
            if (GroundRoots.Contains(hit.collider.transform.root.name))
                return hit.point.y;
            return transform.position.y; // 自己/建筑/道具/他人 → 保持
        }
        // 打空兜底：高空救援——从 30m 找白名单地面；找不到回出生点高度
        if (Physics.RaycastAll(at + Vector3.up * 30f, Vector3.down, 32f, groundMask,
                QueryTriggerInteraction.Ignore)
            .Any(h => GroundRoots.Contains(h.collider.transform.root.name)))
        {
            var g = Physics.RaycastAll(at + Vector3.up * 30f, Vector3.down, 32f, groundMask,
                    QueryTriggerInteraction.Ignore)
                .Where(h => GroundRoots.Contains(h.collider.transform.root.name))
                .OrderBy(h => h.distance).First();
            return g.point.y;
        }
        return _home.y;
    }

    /// <summary>游走/待机切换时暂停、恢复模型的呼吸微动（原地 bob+sway 与位移叠加会抖）。</summary>
    private void SetMotionEnabled(bool on)
    {
        foreach (var motion in GetComponentsInChildren<NpcIdleMotion>(true))
            motion.enabled = on;
    }
}
