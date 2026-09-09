using UnityEngine;
using System.Collections;

/// <summary>
/// NPC 之间互相交流：同区域两个空闲 NPC 靠近时轮流冒气泡寒暄（本地规则短语，不走 LLM）。
/// 与游走让位逻辑兼容：玩家靠近/对话中不触发（玩家优先级最高）。
/// </summary>
[RequireComponent(typeof(NPCController))]
public class NpcChatter : MonoBehaviour
{
    [Header("交流触发")]
    public float chatRange = 3f;        // 两个 NPC 靠近该距离即可能触发
    public float chatCooldown = 20f;    // 同一对 NPC 最小间隔

    [Header("文案")]
    public string[] lines =
    {
        "今天街口蒸糕的香气，隔着两条街都闻得见。",
        "河埠头又来了新货，说是从上海洋行运的。",
        "听说了吗，镇口那牌坊最近要重新描金哩。",
        "傍晚茶馆有说书，去晚了可就没座喽。",
        "这天儿说变就变，出门记得带把伞呐。",
        "西街新开的铺子，账房先生亲自掌柜哩。",
    };

    private NPCController _npc;
    private static readonly System.Collections.Generic.Dictionary<(string, string), float> _pairCooldown = new();

    private void Awake() => _npc = GetComponent<NPCController>();

    private void Start() => StartCoroutine(ChatLoop());

    private IEnumerator ChatLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(Random.Range(4f, 8f));
            // 任意阻塞（对话/演出/放置/玩家在旁）不触发
            if (CinematicIntro.IsCinematic || _npc.InConversation || _npc.PlayerNearby) continue;

            foreach (var other in Object.FindObjectsByType<NPCController>(FindObjectsSortMode.None))
            {
                if (other == _npc || other.InConversation) continue;
                float d = Vector3.Distance(transform.position, other.transform.position);
                if (d > chatRange) continue;
                var key = string.CompareOrdinal(_npc.npcName, other.npcName) < 0
                    ? (_npc.npcName, other.npcName) : (other.npcName, _npc.npcName);
                if (_pairCooldown.TryGetValue(key, out var until) && Time.unscaledTime < until) continue;

                _pairCooldown[key] = Time.unscaledTime + chatCooldown;
                yield return StartCoroutine(Exchange(other));
                break;
            }
        }
    }

    private IEnumerator Exchange(NPCController other)
    {
        // 面向彼此
        FaceTo(other.transform);
        int a = Random.Range(0, lines.Length);
        _npc.ShowBubble(lines[a], 3.5f);
        yield return new WaitForSeconds(3.5f);

        var d = transform.position - other.transform.position; d.y = 0;
        if (d.sqrMagnitude > 0.001f)
            other.transform.rotation = Quaternion.Euler(0f, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 0f);
        int b = Random.Range(0, lines.Length);
        other.ShowBubble(lines[b], 3.5f);
        yield return new WaitForSeconds(3.5f);
    }

    private void FaceTo(Transform t)
    {
        var d = t.position - transform.position; d.y = 0;
        if (d.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.Euler(0f, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 0f);
    }
}
