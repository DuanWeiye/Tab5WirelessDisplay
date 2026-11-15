// 无线显示的配对存储（windisplay/PROTOCOL.md「两边各存什么」）：这台 Tab5 自己的编号和名字、配对过的电脑（最多 4 台）。
// 存在 NVS 的 "wdpair" 名字空间里；闪存没加密，拿到本机的人读得出配对密钥（协议里说明了不挡这个）。
// 各函数哪个线程都能调（无线显示任务、界面、调试台）。
#pragma once

#include <cstddef>
#include <cstdint>

namespace wdpair {

constexpr int    MAX_PEERS = 4;
constexpr size_t ID_LEN = 16, KEY_LEN = 32;
constexpr size_t NAME_LEN = 32;         // 协议里名字一栏的字节数（UTF-8，不足补 0）

struct Entry {
    uint8_t id[ID_LEN];
    char    name[NAME_LEN + 1];         // 清洗过的、只当显示用的文字
};

void self_id(uint8_t out[ID_LEN]);              // 这台 Tab5 的编号：第一次用到时随机取，之后一直是它
void self_name(char out[NAME_LEN + 1]);         // "Tab5-" + MAC 地址的末两个字节

// 找这台电脑的配对密钥。key 可以给空（只问有没有）
bool find(const uint8_t pc_id[ID_LEN], uint8_t key[KEY_LEN]);
// 移除的计数：remove() 移除掉一台、remove_all() 每调一次（哪怕一台都没有）都会让它变大。握手开头记下它，走完 save() 时交回来（见 save）
uint32_t removals();
// 握手走完时调：记下这台电脑这次报的名字，并把它排到「最近连过」的最前面。raw_name 是报文里那 32 个字节，这里面清洗。
// since = 握手开头 removals() 的值。那之后移除过配对：什么都不存、返回 false——人在握手期间移除了配对，握手走完不能把它存回来
// （不分移除的是哪一台：Tab5 同一时刻只有一条连接，握手期间去移除别的电脑的情况不值得为它按编号记）。
// fresh = 这把密钥是这条连接上刚配出来的：没有这台电脑就加上（满了挤掉最久没连过的那一台），有就把密钥换成这一把。
// 不是 fresh（它是照存着的那一把认证过来的）：存着的那一条不在了、或者密钥不是这一把了，也什么都不存、返回 false
bool save(const uint8_t pc_id[ID_LEN], const uint8_t key[KEY_LEN], const uint8_t raw_name[NAME_LEN], bool fresh, uint32_t since);
int  list(Entry out[MAX_PEERS]);                // 最近连过的排前面，返回台数
// 移除：先从闪存里删，删成了才算。删不掉的那一条内存里也留着（重启前后一样，「已配对的电脑」里还看得到它）
bool remove(const uint8_t pc_id[ID_LEN]);       // 没有这一台、或者删不掉，返回 false
bool remove_all();                              // 有删不掉的返回 false

// 对端报的名字只当显示用的文字（协议「握手」表下面的规定）：到第一个 0 为止；不是合法 UTF-8 的字节、控制字符、
// 看不见又能搅乱显示的字符都换成 ?。windisplay/tools/wdproto.py 的 unpack_name、C# 的 Proto.UnpackName 是同一套规矩
void clean_name(const uint8_t raw[NAME_LEN], char out[NAME_LEN + 1]);

}  // namespace wdpair
