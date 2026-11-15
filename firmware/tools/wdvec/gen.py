#!/usr/bin/env python3
"""把 windisplay/testvectors/v2.json 里固件用得上的测试向量转成 C++ 的数组，写到 main/wdisp_vectors.inc。

固件的 `wd selftest` 调试命令（main/wdisp_crypto.cpp 的 wdc::selftest）拿它核对自己的密码学实现：密码 → w、
配对（SPAKE2 的 Tab5 一侧）、会话密钥、逐报文加解密、该拒收的点和公钥。向量文件改了（wd_selftest.py --write-vectors）之后重跑本脚本：
    python3 firmware/tools/wdvec/gen.py
生成的文件入库，不要手改。
"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, '..', '..', '..', 'windisplay', 'testvectors', 'v2.json')
DST = os.path.join(HERE, '..', '..', 'main', 'wdisp_vectors.inc')
HDR = 8     # 报文头的字节数


def arr(hexstr):
    b = bytes.fromhex(hexstr)
    return '{' + ', '.join(f'0x{x:02x}' for x in b) + '}'


def cstr(s):
    return '"' + ''.join(c if 0x20 <= ord(c) <= 0x7E and c not in '"\\' else ''.join(f'\\x{x:02x}""' for x in c.encode('utf-8'))
                         for c in s) + '"'


def main():
    v = json.load(open(SRC, encoding='utf-8'))
    out = ['// 由 firmware/tools/wdvec/gen.py 从 windisplay/testvectors/v2.json 生成，不要手改。', '']
    add = out.append

    add('static const struct { const char* password; uint8_t w[32]; } V_PASSWORDS[] = {')
    for p in v['password']:
        add(f'    {{{cstr(p["password"])}, {arr(p["w"])}}},')
    add('};')
    add('')

    p = v['pairing']
    add('static const struct {')
    add('    const char* password;')
    add('    uint8_t id_pc[16], id_tab5[16], y[32], aad[32], pA[65], pB[65], cA[32], cB[32], key[32], key_id[8];')
    add('} V_PAIRING = {')
    add(f'    {cstr(p["password"])},')
    for k in ('id_pc', 'id_tab5', 'y', 'aad', 'pA', 'pB', 'cA', 'cB', 'pairing_key', 'key_id'):
        add(f'    {arr(p[k])},')
    add('};')
    add('')

    s = v['session']
    hello, auth, auth_ok = (bytes.fromhex(s[k]) for k in ('hello', 'auth', 'auth_ok'))
    pub_pc = auth[HDR + 4 + 16 + 8:HDR + 4 + 16 + 8 + 32]      # AUTH 的载荷：caps、电脑编号、密钥标识、公钥、名字
    pub_tab5 = auth_ok[HDR:]
    add(f'static const uint8_t V_SESSION_HELLO[] = {arr(s["hello"])};')
    add(f'static const uint8_t V_SESSION_AUTH[] = {arr(s["auth"])};')
    add(f'static const uint8_t V_SESSION_AUTH_OK[] = {arr(s["auth_ok"])};')
    add('static const struct { uint8_t key[32], priv_tab5[32], pub_tab5[32], pub_pc[32], dh[32], th[32], k_p2t[32], k_t2p[32]; } V_SESSION = {')
    for h in (s['pairing_key'], s['priv_tab5'], pub_tab5.hex(), pub_pc.hex(), s['dh'], s['th'], s['key_pc_to_tab5'], s['key_tab5_to_pc']):
        add(f'    {arr(h)},')
    add('};')
    add('')

    for i, r in enumerate(v['records']):
        add(f'static const uint8_t V_REC{i}_WIRE[] = {arr(r["wire"])};')
    add('static const struct { const char* what; uint8_t key[32]; uint64_t count; const uint8_t* wire; size_t wire_len; } V_RECORDS[] = {')
    for i, r in enumerate(v['records']):
        assert r['wire'].startswith(r['header'])
        add(f'    {{{cstr(r["what"])}, {arr(r["key"])}, {r["count"]}ULL, V_REC{i}_WIRE, sizeof V_REC{i}_WIRE}},')
    add('};')
    # 明文载荷单独给：解出来要和它一样
    for i, r in enumerate(v['records']):
        add(f'static const uint8_t V_REC{i}_PLAIN[] = {arr(r["payload"]) if r["payload"] else "{0}"};')
    add('static const uint8_t* const V_RECORD_PLAIN[] = {' + ', '.join(f'V_REC{i}_PLAIN' for i in range(len(v['records']))) + '};')
    add('')

    rj = v['reject']
    for i, pt in enumerate(rj['points']):
        add(f'static const uint8_t V_BADPT{i}[] = {arr(pt["point"])};')
    add('static const struct { const char* what; const uint8_t* p; size_t n; } V_BAD_POINTS[] = {')
    for i, pt in enumerate(rj['points']):
        add(f'    {{{cstr(pt["what"])}, V_BADPT{i}, sizeof V_BADPT{i}}},')
    add('};')
    k = rj['k_infinity']
    assert k['w'] == v['password'][0]['w']      # 这一组用的是第一个密码的 w
    add(f'static const uint8_t V_KINF_PA[65] = {arr(k["pA_for_tab5"])};     // 配上第一个密码，K 是无穷远点')
    add('static const struct { const char* what; uint8_t pub[32]; } V_BAD_X25519[] = {')
    for x in rj['x25519_pub']:
        add(f'    {{{cstr(x["what"])}, {arr(x["pub"])}}},')
    add('};')

    with open(DST, 'w', encoding='utf-8') as f:
        f.write('\n'.join(out) + '\n')
    print(f'写了 {os.path.normpath(DST)}（{len(out)} 行）')


if __name__ == '__main__':
    main()
