# 汇总帧探针：按 (页面, 各调试开关状态) 分段，统计大面积帧的 render/total/dt 中位数。
# 分段依据：ser.sh 插入的 "@@CMD" 标记 + 设备打印的 "benchpage variant="。
# 用法: python3 summ.py <串口日志> [最小像素数, 默认 400000]
import re, sys, statistics
log = open(sys.argv[1], errors="replace").read()
log = re.sub(r"\x1b\[[0-9;]*m", "", log)
minpx = int(sys.argv[2]) if len(sys.argv) > 2 else 400000
st = {"page": "?", "accel": "on", "pipe": "0", "dbuf": "0", "hog": "off", "maskram": "0", "lpclaim": "off", "extmem": "16384"}
groups, order = {}, []
for line in log.splitlines():
    m = re.match(r"@@CMD (.*)", line)
    if m:
        c = m.group(1).strip()
        if c == "$": st["page"] = "设置"
        elif c == "@": st["page"] = "相册"
        mm = re.match(r"disp (accel|pipe|dbuf|hog|maskram|lpclaim|extmem) (\S+)", c)
        if mm: st[mm.group(1)] = mm.group(2)
        continue
    m = re.search(r"benchpage variant=(\w)", line)
    if m: st["page"] = m.group(1); continue
    m = re.search(r"frame px=(\d+) render=([\d.]+)ms total=([\d.]+)ms dt=([\d.]+)ms rot=([\d.]+)ms\(px=(\d+)\) flush=(\d+)/([\d.]+)ms", line)
    if m and int(m.group(1)) > minpx:
        k = tuple(st.values())
        if k not in groups: groups[k] = []; order.append(k)
        groups[k].append((float(m.group(2)), float(m.group(3)), float(m.group(4))))
print("页面 | accel pipe dbuf hog maskram lpclaim extmem |  n | render中位 | total中位 | dt中位(排除>200)")
for k in order:
    g = groups[k]
    dts = [x[2] for x in g if x[2] < 200]
    print("%-4s | %5s %4s %4s %3s %7s %7s %6s | %2d | %8.1f | %8.1f | %8.1f" % (k[0], k[1], k[2], k[3], k[4], k[5], k[6], k[7], len(g),
          statistics.median(x[0] for x in g), statistics.median(x[1] for x in g),
          statistics.median(dts) if dts else -1))
