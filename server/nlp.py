"""Luanti Builder - nlp 模块。"""
import re

# ============================================================
# NLP 解析器
# ============================================================

BUILDING_TYPES = {
    "城堡":"castle","castle":"castle","fortress":"castle","洋楼":"castle","老洋楼":"castle",
    "房子":"house","房屋":"house","小屋":"house","house":"house","hut":"house","cabin":"house",
    "塔":"tower","tower":"tower","高塔":"tower",
    "金字塔":"pyramid","pyramid":"pyramid",
    "桥":"bridge","桥梁":"bridge","bridge":"bridge",
    "花园":"garden","garden":"garden","庭院":"garden",
    "神殿":"temple","寺庙":"temple","temple":"temple",
    "雕像":"statue","statue":"statue","雕塑":"statue",
    "喷泉":"fountain","fountain":"fountain",
    "灯塔":"lighthouse","lighthouse":"lighthouse",
    "城墙":"wall","wall":"wall","围墙":"wall",
    "树":"tree","tree":"tree","大树":"tree",
    "飞船":"spaceship","spaceship":"spaceship","火箭":"rocket",
    "蘑菇":"mushroom","mushroom":"mushroom",
    "心形":"heart","heart":"heart","爱心":"heart",
    "球体":"sphere","sphere":"sphere","球":"sphere",
    "螺旋":"spiral","spiral":"spiral",
    "上海":"shanghai","shanghai":"shanghai",
    "村庄":"village","village":"village",
    "宫殿":"castle","palace":"castle","教堂":"temple","cathedral":"temple",
    "风车":"windmill","windmill":"windmill","亭子":"gazebo","gazebo":"gazebo","凉亭":"gazebo",
    "宝塔":"pagoda","pagoda":"pagoda","摩天大楼":"skyscraper","skyscraper":"skyscraper","高楼":"skyscraper",
    "骑楼":"qilou","qilou":"qilou","牌坊":"paifang","paifang":"paifang","牌楼":"paifang","拱门":"paifang",
    "戏台":"xitai","戏楼":"xitai","舞台":"xitai","xitai":"xitai",
    "鼓楼":"gulou","钟楼":"gulou","gulou":"gulou",
}

COLOR_MAP = {
    "红色":"red","红":"red","red":"red",
    "蓝色":"blue","蓝":"blue","blue":"blue",
    "黄色":"yellow","黄":"yellow","yellow":"yellow","金色":"yellow","gold":"yellow",
    "绿色":"green","绿":"green","green":"green",
    "白色":"white","白":"white","white":"white",
    "黑色":"black","黑":"black","black":"black",
    "橙色":"orange","橙":"orange","orange":"orange",
    "紫色":"purple","紫":"purple","purple":"purple",
    "粉色":"pink","粉":"pink","pink":"pink",
    "青色":"cyan","青":"cyan","cyan":"cyan",
    "灰色":"gray","灰":"gray","gray":"gray","银色":"gray","silver":"gray","棕色":"orange","棕":"orange","brown":"orange",
}

SIZE_MAP = {
    "巨大":3,"超大":3,"huge":3,"giant":3,"massive":3,
    "大":2,"大型":2,"large":2,"big":2,
    "中等":1,"medium":1,"normal":1,
    "小":0,"小型":0,"small":0,"tiny":0,"mini":0,
}

MATERIAL_MAP = {
    "石头":"stone","石":"stone","stone":"stone","rock":"stone",
    "木头":"wood","木":"wood","wood":"wood","wooden":"wood",
    "砖":"brick","砖块":"brick","brick":"brick",
    "沙":"sand","沙子":"sand","sand":"sand",
    "玻璃":"glass","glass":"glass",
    "金属":"iron","metal":"iron","iron":"iron",
    "泥土":"dirt","dirt":"dirt",
    "青砖":"stone","青石":"stone",  # 民国青砖=青灰石（先于"青"色命中）
    "雪":"snow","snow":"snow","水晶":"glass","crystal":"glass","钢铁":"iron","steel":"iron","钢":"iron","竹":"wood","竹子":"wood",
}

FEATURES_MAP = {
    "塔楼":"towers","tower":"towers","尖塔":"towers",
    "护城河":"moat","moat":"moat",  # 不再生成水，只标记特征
    "花园":"garden","garden":"garden",
    "大门":"gate","gate":"gate","门":"gate",
    "窗户":"windows","window":"windows",
    "屋顶":"roof","roof":"roof",
    "灯光":"lights","light":"lights","发光":"lights",
    "旗":"flag","flag":"flag","旗帜":"flag",
    "楼梯":"stairs","stair":"stairs",
}

def _match_longest(text, mapping):
    """优先匹配最长关键词（避免"宝塔"被"塔"先匹配）"""
    hits = [(k, v) for k, v in mapping.items() if k in text]
    if not hits:
        return None
    hits.sort(key=lambda x: len(x[0]), reverse=True)
    return hits[0][1]

def parse_input(text):
    tl = text.lower()
    # 复合词预归一化："青砖/青石"是材质不是颜色，先摘除避免"青"误命中 cyan
    material_hints = []
    for word in ("青砖", "青石"):
        if word in tl:
            material_hints.append(word)
            tl = tl.replace(word, "")
    result = {"type":None,"color":None,"size":1,"material":None,"features":[],"raw":text}
    result["type"] = _match_longest(tl, BUILDING_TYPES)
    result["color"] = _match_longest(tl, COLOR_MAP)
    result["size"] = _match_longest(tl, SIZE_MAP) or 1
    result["material"] = _match_longest(tl, MATERIAL_MAP) or ("stone" if material_hints else None)
    for k,v in FEATURES_MAP.items():
        if k in tl and v not in result["features"]: result["features"].append(v)
    return result
