# -*- coding: utf-8 -*-
# STS2 v0.111.0 种子 X6MB8T8NLGQ5 (Silent, A0) 沿路线A的完整推演
# 算法转写自 .reference/sts2-v0.111.0；RNG 状态/队列/遗物袋取自无头引擎存档
import json, struct, re, glob

M = (1 << 64) - 1
def rotl(x, r): return ((x << r) | (x >> (64 - r))) & M
def f32(x): return struct.unpack('f', struct.pack('f', x))[0]

P1 = 0x9E3779B185EBCA87; P2 = 0xC2B2AE3D27D4EB4F; P3 = 0x165667B19E3779F9
P4 = 0x85EBCA77C2B2AE63; P5 = 0x27D4EB2F165667C5
def xxh64(data: bytes, seed: int = 0) -> int:
    n = len(data); i = 0
    if n >= 32:
        v = [(seed + P1 + P2) & M, (seed + P2) & M, seed & M, (seed - P1) & M]
        while i + 32 <= n:
            for k in range(4):
                lane = int.from_bytes(data[i:i+8], 'little'); i += 8
                v[k] = (rotl((v[k] + lane * P2) & M, 31) * P1) & M
        acc = (rotl(v[0], 1) + rotl(v[1], 7) + rotl(v[2], 12) + rotl(v[3], 18)) & M
    else:
        acc = (seed + P5) & M
    acc = (acc + n) & M
    while i + 8 <= n:
        lane = int.from_bytes(data[i:i+8], 'little'); i += 8
        acc = (rotl((acc ^ (rotl((lane * P2) & M, 31) * P1 & M)) & M, 27) * P1 + P4) & M
    if i + 4 <= n:
        lane = int.from_bytes(data[i:i+4], 'little'); i += 4
        acc = (rotl((acc ^ (lane * P1 & M)) & M, 23) * P2 + P3) & M
    while i < n:
        acc = (rotl((acc ^ (data[i] * P5)) & M, 11) * P1) & M
        i += 1
    acc ^= acc >> 33; acc = (acc * P2) & M
    acc ^= acc >> 29; acc = (acc * P3) & M
    acc ^= acc >> 32
    return acc & M
assert xxh64(b"") == 0xEF46DB3751D8E999

class Xoshiro:
    def __init__(self, seed: int):
        x = seed & M
        def sm():
            nonlocal x
            x = (x + 11400714819323198485) & M
            n = x
            n = ((n ^ (n >> 30)) * 13787848793156543929) & M
            n = ((n ^ (n >> 27)) * 10723151780598845931) & M
            return n ^ (n >> 31)
        self.s0, self.s1, self.s2, self.s3 = sm(), sm(), sm(), sm()
    @classmethod
    def from_state(cls, s):
        o = cls.__new__(cls)
        o.s0, o.s1, o.s2, o.s3 = s['s0'], s['s1'], s['s2'], s['s3']
        return o
    def next_ulong(self):
        s = self.s0; s2 = self.s1; s3 = self.s2; s4 = self.s3
        result = (rotl((s2 * 5) & M, 7) * 9) & M
        num = (s2 << 17) & M
        s3 ^= s; s4 ^= s2; s2 ^= s3; s ^= s4; s3 ^= num
        s4 = rotl(s4, 45)
        self.s0, self.s1, self.s2, self.s3 = s, s2, s3, s4
        return result
    def next_double(self): return (self.next_ulong() >> 11) * 2.0 ** -53
    def next_float(self): return (self.next_ulong() >> 40) * 2.0 ** -24
    def next_int(self, lo, hi): return lo + int(self.next_double() * (hi - lo))

def snake(name): return re.sub(r'(?<!^)(?=[A-Z])', '_', name).lower()

# ---------- 校验 ----------
SAVE = r'C:/Users/slimoon/AppData/Local/Temp/sts2_X6MB8T8NLGQ5.json'
save = json.load(open(SAVE, encoding='utf-8'))
p = save['players'][0]
seed_str = 'X6MB8T8NLGQ5'
seed_u = xxh64(seed_str.encode())
ok = seed_u == p['rng']['seed']
print(f"[校验] XxHash64('{seed_str}') == 存档主种子: {'PASS' if ok else 'FAIL'} ({seed_u})")
for stream in ['rewards', 'shops', 'transformations']:
    x = Xoshiro((seed_u + xxh64(snake(stream).encode())) & M)
    ref = p['rng']['rngs'][stream]
    ok2 = (x.s0, x.s1, x.s2, x.s3) == (ref['s0'], ref['s1'], ref['s2'], ref['s3'])
    print(f"[校验] {stream} 流状态重建: {'PASS' if ok2 else 'FAIL'}")
    if not ok2: raise SystemExit('哈希管线校验失败, 终止')

# ---------- 本地化 ----------
LOC = 'D:/FORSTS2/sts2-cli/localization_zhs/'
def load(f): return json.load(open(LOC + f, encoding='utf-8'))
cards_l, potions_l, enc_l, ev_l, anc_l, relics_l = (load(x) for x in
    ['cards.json', 'potions.json', 'encounters.json', 'events.json', 'ancients.json', 'relics.json'])
def clean(s): return re.sub(r'\[/?\w+\]', '', s or '')
def card_zh(c): return clean(cards_l.get(snake(c) + '.title', c))
def potion_zh(c): return clean(potions_l.get(snake(c) + '.title', c))
def relic_zh(rid): return clean(relics_l.get(rid.split('.', 1)[1] + '.name', rid))
def enc_zh(eid): return clean(enc_l.get(eid.split('.', 1)[1] + '.title', eid))
def ev_zh(eid): return clean(ev_l.get(eid.split('.', 1)[1] + '.title', eid))

# ---------- 卡池 (SilentCardPool.cs 声明顺序 91张, Epoch全解锁) ----------
CARD_POOL = """Abrasive Accelerant Accuracy Acrobatics Adrenaline Afterimage Anticipate Assassinate Backflip
Backstab BladeOfInk BladeDance BladeSymphony Blur BouncingFlask BubbleBubble BulletTime Burst CalculatedGamble
CloakAndDagger CorrosiveWave Concoct DaggerSpray DaggerThrow Dash DeadlyPoison DefendSilent Deflect DodgeAndRoll
EchoingSlash Envenom EscapePlan Expertise Expose Fade FanOfKnives Finisher Flanking Flechettes FlickFlack Sidestep
Footwork GrandFinale HandTrick Haze HiddenDaggers InfiniteBlades KnifeTrap LeadingStrike LegSweep Malaise
MasterPlanner MementoMori Mirage Murder Neutralize Nightmare NoxiousFumes Outbreak PhantomBlades PiercingWail
Pinpoint PoisonedStab Pounce PreciseCut Predator Prepared Reflex Ricochet SerpentForm ShadowStep Shadowmeld Skewer
Slice Snakebite Sneaky Speedster StormOfSteel Strangle StrikeSilent SuckerPunch Suppress Survivor Tactician TheHunt
ToolsOfTheTrade Tracking Untouchable UpMySleeve WellLaidPlans WraithForm""".split()
CARD_DIR = 'D:/FORSTS2/.reference/sts2-v0.111.0/MegaCrit.Sts2.Core.Models.Cards'
CARD_RARITY = {}
for c in CARD_POOL:
    src = open(f'{CARD_DIR}/{c}.cs', encoding='utf-8').read()
    CARD_RARITY[c] = re.search(r'CardRarity\.(\w+)', src).group(1)

# ---------- 药水池 ----------
POT_DIR = 'D:/FORSTS2/.reference/sts2-v0.111.0/MegaCrit.Sts2.Core.Models.Potions'
def prarity(c):
    return re.search(r'PotionRarity\.(\w+)', open(f'{POT_DIR}/{c}.cs', encoding='utf-8').read()).group(1)
POTIONS = ['PoisonPotion', 'GhostInAJar', 'CunningPotion'] + """AttackPotion BeetleJuice BlessingOfTheForge BlockPotion
BottledPotential Clarity ColorlessPotion CureAll DexterityPotion DistilledChaos DropletOfPrecognition Duplicator
EnergyPotion EntropicBrew ExplosiveAmpoule FairyInABottle FirePotion FlexPotion Fortifier FruitJuice FyshOil
GamblersBrew GigantificationPotion HeartOfIron LiquidBronze LiquidMemories LuckyTonic MazalethsGift OrobicAcid
PotionOfBinding PowderedDemise PowerPotion RadiantTincture RegenPotion ShacklingPotion ShipInABottle SkillPotion
SneckoOil SpeedPotion StableSerum StrengthPotion SwiftPotion TouchOfInsanity VulnerablePotion WeakPotion""".split()
POT_RARITY = {c: prarity(c) for c in POTIONS}

# ---------- 流 ----------
rew = Xoshiro.from_state(p['rng']['rngs']['rewards'])
trr = Xoshiro((seed_u + xxh64(b'treasure_room_relics')) & M)
ump = Xoshiro((seed_u + xxh64(b'unknown_map_point')) & M)
card_pity = f32(p['odds']['card_rarity_odds_value'])      # -0.05
pot_pity = f32(p['odds']['potion_reward_odds_value'])     # 0.40
U_BASE = {'Monster': 0.1, 'Elite': -1.0, 'Treasure': 0.02, 'Shop': 0.03}
U_ODDS = dict(U_BASE)
bag = {k: list(v) for k, v in p['relic_grab_bag']['relic_id_lists'].items()}

a1 = save['acts'][0]['rooms']
nq, eq, evq = a1['normal_encounter_ids'], a1['elite_encounter_ids'], a1['event_ids']
n_ptr = a1['normal_encounters_visited']; e_ptr = a1['elite_encounters_visited']; ev_ptr = a1['events_visited']

def roll_rarity(odds_type):
    global card_pity
    rare_base = {'RegularEncounter': 0.03, 'EliteEncounter': 0.10, 'BossEncounter': 1.0}[odds_type]
    unc_base = {'RegularEncounter': 0.37, 'EliteEncounter': 0.40, 'BossEncounter': 0.0}[odds_type]
    num = rew.next_float()
    off = 0.0 if odds_type == 'BossEncounter' else card_pity
    rare_th = f32(f32(rare_base) + off)
    if num < rare_th: r = 'Rare'
    elif num < f32(f32(unc_base) + rare_th): r = 'Uncommon'
    else: r = 'Common'
    card_pity = f32(-0.05) if r == 'Rare' else f32(card_pity + f32(0.01))
    return r, num

def roll_potion(elite=False):
    global pot_pity
    th = f32(pot_pity + f32(0.125)) if elite else pot_pity
    r = rew.next_float()
    hit = r < th
    pot_pity = f32(pot_pity - f32(0.1)) if hit else f32(pot_pity + f32(0.1))
    if not hit: return None
    pr = rew.next_float()
    rar = 'Rare' if pr <= f32(0.1) else ('Uncommon' if pr <= f32(0.35) else 'Common')
    cand = [c for c in POTIONS if POT_RARITY[c] == rar]
    pick = cand[rew.next_int(0, len(cand))]
    return f'{potion_zh(pick)}({rar})'

def fight(kind='normal'):
    """战斗奖励: 药水判定 -> 金币 -> (药水) -> 3卡 -> (精英:遗物). 返回描述."""
    global n_ptr, e_ptr
    out = []
    pot = roll_potion(elite=(kind == 'elite'))
    if kind == 'normal':
        eid = nq[n_ptr % len(nq)]; n_ptr += 1
        gold = rew.next_int(10, 21)
        out.append(f'金币{gold} | 药水: {pot or "无"}')
    else:
        eid = eq[e_ptr % len(eq)]; e_ptr += 1
        gold = rew.next_int(35, 46)
        out.append(f'金币{gold} | 药水: {pot or "无"}')
    cards = []
    for i in range(3):
        r, num = roll_rarity('EliteEncounter' if kind == 'elite' else 'RegularEncounter')
        cand = [c for c in CARD_POOL if c not in cards and CARD_RARITY[c] == r]
        pick = cand[rew.next_int(0, len(cand))]
        cards.append(pick)
        rew.next_float()  # 升级掷骰: act0 阈值 0, 恒不升级, 但消耗照旧
    out.append('卡奖: ' + ', '.join(f'{card_zh(c)}[{CARD_RARITY[c][0]}]' for c in cards))
    if kind == 'elite':
        rr = rew.next_float()
        rar = 'Common' if rr < f32(0.5) else ('Uncommon' if rr < f32(0.83) else 'Rare')
        b = bag.get(rar.lower()) or bag.get(rar)
        relic = b.pop(0) if b else '(袋空回退)'
        out.append(f'精英遗物[{rar}]: {relic_zh(relic)}')
    return f'{enc_zh(eid)}: ' + ' | '.join(out)

def unknown(node):
    global ev_ptr
    num = ump.next_float()
    acc = 0.0; result = 'Event'
    for k in ['Monster', 'Elite', 'Treasure', 'Shop']:
        if U_ODDS[k] >= 0:
            acc += U_ODDS[k]
            if num <= acc:
                result = k; break
    for k in U_BASE:  # 掷中: 重置为基础值; 未掷中: 累加基础值(含负值, Elite 恒 -1)
        U_ODDS[k] = U_BASE[k] if k == result else U_ODDS[k] + U_BASE[k]
    if result == 'Event':
        e = evq[ev_ptr % len(evq)]; ev_ptr += 1
        return f'事件: {ev_zh(e)}'
    return f'→ {result} 房'

def treasure():
    g = rew.next_int(42, 53)
    num = trr.next_float()
    rar = 'Common' if num < f32(0.5) else ('Uncommon' if num < f32(0.83) else 'Rare')
    b = bag.get(rar.lower()) or bag.get(rar)
    relic = b.pop(0) if b else '(袋空回退)'
    return f'金币{g} + 遗物[{rar}]: {relic_zh(relic)}'

print('\n=== 路线A: 左线·早精英 (N→r1c2→r2c1→r3c0→r4c0→r5c0?→r6c0精英→r7c1→r8c1?→r9c1宝箱→r10c0篝火→r11c0→r12c0商店→r13c0?→r14c0?→r15c1篝火→Boss) ===')
for node in ['r1c2', 'r2c1', 'r3c0', 'r4c0']:
    print(f'  {node:>7} 怪  : {fight()}')
r = unknown('r5c0')
print(f'  {"r5c0":>7} ?   : {r}')
if r.startswith('→ Monster'):
    print(f'  {"":>13}└ 计划外战斗: {fight()}')
print(f'  {"r6c0":>7} 精英: {fight("elite")}')
print(f'  {"r7c1":>7} 怪  : {fight()}')
r = unknown('r8c1')
print(f'  {"r8c1":>7} ?   : {r}')
if r.startswith('→ Monster'):
    print(f'  {"":>13}└ 计划外战斗: {fight()}')
print(f'  {"r9c1":>7} 宝箱: {treasure()}')
print('  {"r10c0":>7} 篝火: 无 RNG (回血或升级)')
print(f'  {"r11c0":>7} 怪  : {fight()}')
print('  r12c0 商店: 库存由 Shops 流 + Rewards 稀有度掷骰决定(未展开), 其后 Rewards 流继续位移')
for node in ['r13c0', 'r14c0']:
    print(f'  {node:>7} ?   : {unknown(node)}  (商店后奖励数值不再展开, 此处仅事件身份)')
print(f'  {"r16c3":>7} Boss: {enc_zh(a1["boss_id"])}')

# ---------- 队列身份总览 ----------
print('\n=== 第一幕 遭遇队列 (按访问顺序取用) ===')
print('  普通战斗队列: ' + ' → '.join(enc_zh(e) for e in nq))
print('  精英队列(前6): ' + ' → '.join(enc_zh(e) for e in eq[:6]))
print('  事件队列(前8, 游标从1起): ' + ' → '.join(ev_zh(e) for e in evq[1:9]))
print(f'  Boss: {enc_zh(a1["boss_id"])}')
for i, act in enumerate(save['acts'][1:], start=2):
    r = act['rooms']
    an = r['ancient_id'].split('.', 1)[1]
    print(f'=== 第{i}幕 {act["id"].split(".",1)[1]} ===')
    print(f'  先古: {clean(anc_l.get(an + ".title", an))} | Boss: {enc_zh(r["boss_id"])} | 双Boss: {r.get("second_boss_id")}')
    print(f'  弱怪开场: ' + ' → '.join(enc_zh(e) for e in r['normal_encounter_ids'] if e.endswith("_WEAK")))
    print(f'  首精英: {enc_zh(r["elite_encounter_ids"][0])}')
