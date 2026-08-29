using System.Collections;
using UnityEngine;

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
    [Tooltip("移动速度（米/秒），镇民散步节奏")]
    public float walkSpeed = 1.2f;

    [Header("节奏")]
    [Tooltip("每次站桩时长区间（秒）")]
    public Vector2 idleTime = new Vector2(2f, 5f);
    [Tooltip("单段路超时（秒），到点强制换目标防卡死")]
    public float legTimeout = 8f;

    [Header("贴地")]
    [Tooltip("向下 Raycast 层掩码（默认 Everything，起点抬高避开自身）")]
    public LayerMask groundMask = ~0;

    private NPCController _npc;
    private Vector3 _home;
    private Vector3 _target;
    private float _stateUntil;
    private bool _walking;
    private const float ArriveDist = 0.35f;
    private const float GroundRayLift = 2.5f;   // 起点抬高（越过模型顶）再向下打

    private void Awake()
    {
        _npc = GetComponent<NPCController>();
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
        for (int i = 0; i < 4; i++) // 多试几次避开太贴身的点
        {
            float a = Random.value * Mathf.PI * 2f;
            float r = Mathf.Sqrt(Random.value) * radius; // 面积均匀分布
            var p = _home + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            // 目标离玩家太近会显得"贴脸游走"，重新抽
            if (_npc.PlayerTransform != null &&
                (p - _npc.PlayerTransform.position).sqrMagnitude < 4f) continue;
            _target = p;
            break;
        }
        _walking = true;
        _stateUntil = Time.unscaledTime + legTimeout;
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
        pos.y = GroundY(pos);
        transform.position = pos;

        // 行走朝向：面向移动方向（NPCController 的 SmoothDamp 只在玩家附近才接管，
        // 远离时这里用直接欧拉角即可，转角平缓无跳变）
        float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        var e = transform.eulerAngles;
        transform.rotation = Quaternion.Euler(0f, Mathf.MoveTowardsAngle(e.y, yaw, 120f * Time.deltaTime), 0f);
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

    /// <summary>向下 Raycast 求地面高度；打空则保持当前 y（平地兜底）。</summary>
    private float GroundY(Vector3 at)
    {
        Vector3 origin = at + Vector3.up * GroundRayLift;
        if (Physics.Raycast(origin, Vector3.down, out var hit, GroundRayLift + 2f, groundMask,
                QueryTriggerInteraction.Ignore))
        {
            // 排除命中自身碰撞体
            if (hit.collider.transform.root == transform.root)
                return transform.position.y;
            return hit.point.y;
        }
        return transform.position.y;
    }

    /// <summary>游走/待机切换时暂停、恢复模型的呼吸微动（原地 bob+sway 与位移叠加会抖）。</summary>
    private void SetMotionEnabled(bool on)
    {
        foreach (var motion in GetComponentsInChildren<NpcIdleMotion>(true))
            motion.enabled = on;
    }
}
