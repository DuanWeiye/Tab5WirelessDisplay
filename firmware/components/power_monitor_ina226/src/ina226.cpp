/*

The MIT License

Copyright (c) 2014-2023 Korneliusz Jarzębski

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/

#include <math.h>
#include <atomic>
#include <esp_log.h>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include "ina226.hpp"

#define I2C_MASTER_TIMEOUT_MS 50  // 超时时间

bool INA226::begin(i2c_master_bus_handle_t bus_handle, uint8_t address)
{
    inaAddress = address;

    i2c_device_config_t dev_cfg = {
        .dev_addr_length = I2C_ADDR_BIT_LEN_7,
        .device_address  = address,
        .scl_speed_hz    = 400000,
    };
    ESP_ERROR_CHECK(i2c_master_bus_add_device(bus_handle, &dev_cfg, &i2c_dev_handle_ina226));

    if (i2c_dev_handle_ina226 == NULL) {
        return false;
    }

    return true;
}

bool INA226::configure(ina226_averages_t avg, ina226_busConvTime_t busConvTime, ina226_shuntConvTime_t shuntConvTime,
                       ina226_mode_t mode)
{
    uint16_t config = 0;

    config |= (avg << 9 | busConvTime << 6 | shuntConvTime << 3 | mode);

    vBusMax   = 36;
    vShuntMax = 0.08192f;

    writeRegister16(INA226_REG_CONFIG, config);

    return true;
}

bool INA226::calibrate(float rShuntValue, float iMaxExpected)
{
    uint16_t calibrationValue;
    rShunt = rShuntValue;

    // float iMaxPossible;
    float minimumLSB;

    // iMaxPossible = vShuntMax / rShunt;

    minimumLSB = iMaxExpected / 32767;

    currentLSB = (uint32_t)(minimumLSB * 100000000);
    currentLSB /= 100000000;
    currentLSB /= 0.0001;
    currentLSB = ceil(currentLSB);
    currentLSB *= 0.0001;

    powerLSB = currentLSB * 25;

    calibrationValue = (uint16_t)((0.00512) / (currentLSB * rShunt));

    writeRegister16(INA226_REG_CALIBRATION, calibrationValue);

    return true;
}

float INA226::getMaxPossibleCurrent(void)
{
    return (vShuntMax / rShunt);
}

float INA226::getMaxCurrent(void)
{
    float maxCurrent  = (currentLSB * 32767);
    float maxPossible = getMaxPossibleCurrent();

    if (maxCurrent > maxPossible) {
        return maxPossible;
    } else {
        return maxCurrent;
    }
}

float INA226::getMaxShuntVoltage(void)
{
    float maxVoltage = getMaxCurrent() * rShunt;

    if (maxVoltage >= vShuntMax) {
        return vShuntMax;
    } else {
        return maxVoltage;
    }
}

float INA226::getMaxPower(void)
{
    return (getMaxCurrent() * vBusMax);
}

bool INA226::readBusPower(float& watts)
{
    int16_t raw;
    if (!readRegister16(INA226_REG_POWER, raw)) return false;
    watts = raw * powerLSB;
    return true;
}

bool INA226::readShuntCurrent(float& amps)
{
    int16_t raw;
    if (!readRegister16(INA226_REG_CURRENT, raw)) return false;
    amps = raw * currentLSB;
    return true;
}

bool INA226::readRawShuntCurrent(int16_t& raw)
{
    return readRegister16(INA226_REG_CURRENT, raw);
}

bool INA226::readShuntVoltage(float& volts)
{
    int16_t raw;
    if (!readRegister16(INA226_REG_SHUNTVOLTAGE, raw)) return false;
    volts = raw * 0.0000025f;
    return true;
}

bool INA226::readBusVoltage(float& volts)
{
    int16_t raw;
    if (!readRegister16(INA226_REG_BUSVOLTAGE, raw)) return false;
    volts = raw * 0.00125f;
    return true;
}

ina226_averages_t INA226::getAverages(void)
{
    uint16_t value;

    value = readConfig16(INA226_REG_CONFIG);
    value &= 0b0000111000000000;
    value >>= 9;

    return (ina226_averages_t)value;
}

ina226_busConvTime_t INA226::getBusConversionTime(void)
{
    uint16_t value;

    value = readConfig16(INA226_REG_CONFIG);
    value &= 0b0000000111000000;
    value >>= 6;

    return (ina226_busConvTime_t)value;
}

ina226_shuntConvTime_t INA226::getShuntConversionTime(void)
{
    uint16_t value;

    value = readConfig16(INA226_REG_CONFIG);
    value &= 0b0000000000111000;
    value >>= 3;

    return (ina226_shuntConvTime_t)value;
}

ina226_mode_t INA226::getMode(void)
{
    uint16_t value;

    value = readConfig16(INA226_REG_CONFIG);
    value &= 0b0000000000000111;

    return (ina226_mode_t)value;
}

void INA226::setMaskEnable(uint16_t mask)
{
    writeRegister16(INA226_REG_MASKENABLE, mask);
}

uint16_t INA226::getMaskEnable(void)
{
    return readConfig16(INA226_REG_MASKENABLE);
}

void INA226::enableShuntOverLimitAlert(void)
{
    writeRegister16(INA226_REG_MASKENABLE, INA226_BIT_SOL);
}

void INA226::enableShuntUnderLimitAlert(void)
{
    writeRegister16(INA226_REG_MASKENABLE, INA226_BIT_SUL);
}

void INA226::enableBusOvertLimitAlert(void)
{
    writeRegister16(INA226_REG_MASKENABLE, INA226_BIT_BOL);
}

void INA226::enableBusUnderLimitAlert(void)
{
    writeRegister16(INA226_REG_MASKENABLE, INA226_BIT_BUL);
}

void INA226::enableOverPowerLimitAlert(void)
{
    writeRegister16(INA226_REG_MASKENABLE, INA226_BIT_POL);
}

void INA226::enableConversionReadyAlert(void)
{
    writeRegister16(INA226_REG_MASKENABLE, INA226_BIT_CNVR);
}

void INA226::disableAlerts(void)
{
    writeRegister16(INA226_REG_MASKENABLE, 0);
}

void INA226::setBusVoltageLimit(float voltage)
{
    uint16_t value = voltage / 0.00125;
    writeRegister16(INA226_REG_ALERTLIMIT, value);
}

void INA226::setShuntVoltageLimit(float voltage)
{
    uint16_t value = voltage / 0.0000025;
    writeRegister16(INA226_REG_ALERTLIMIT, value);
}

void INA226::setPowerLimit(float watts)
{
    uint16_t value = watts / powerLSB;
    writeRegister16(INA226_REG_ALERTLIMIT, value);
}

void INA226::setAlertInvertedPolarity(bool inverted)
{
    uint16_t temp = getMaskEnable();

    if (inverted) {
        temp |= INA226_BIT_APOL;
    } else {
        temp &= ~INA226_BIT_APOL;
    }

    setMaskEnable(temp);
}

void INA226::setAlertLatch(bool latch)
{
    uint16_t temp = getMaskEnable();

    if (latch) {
        temp |= INA226_BIT_LEN;
    } else {
        temp &= ~INA226_BIT_LEN;
    }

    setMaskEnable(temp);
}

bool INA226::isMathOverflow(void)
{
    return ((getMaskEnable() & INA226_BIT_OVF) == INA226_BIT_OVF);
}

bool INA226::isAlert(void)
{
    return ((getMaskEnable() & INA226_BIT_AFF) == INA226_BIT_AFF);
}

uint16_t INA226::readConfig16(uint8_t reg)
{
    int16_t val = 0;
    readRegister16(reg, val);
    return (uint16_t)val;
}

bool INA226::readRegister16(uint8_t reg, int16_t& val)
{
    uint8_t r_buffer[2] = {0};
    esp_err_t err = i2c_master_transmit_receive(i2c_dev_handle_ina226, &reg, 1, r_buffer, 2, I2C_MASTER_TIMEOUT_MS);
    if (err != ESP_OK) {
        // 读失败：返回 false，由调用方丢弃这次采样（见 board::readPower 的 valid）。
        // 这里把失败原因和所在任务打出来：驱动层那行 "I2C transaction failed" 不带这两样，光看它分不清是谁、为什么。
        // 限流：芯片或总线持续不应答时，几个任务加起来每秒要读好几次（其中一部分在 LVGL 任务里），
        // 逐条打会刷屏并拖慢渲染。所以第一次失败立刻打，之后每 10 秒最多一条，并带上这期间的失败次数。
        static std::atomic<uint32_t> s_fails{0};       // 上一条告警之后累计的失败次数
        static std::atomic<uint32_t> s_last_tick{0};   // 上一条告警的时刻；0 = 还没打过
        s_fails.fetch_add(1);
        const uint32_t now  = (uint32_t)xTaskGetTickCount();
        uint32_t       last = s_last_tick.load();
        if ((last == 0 || now - last >= pdMS_TO_TICKS(10000)) &&
            s_last_tick.compare_exchange_strong(last, now ? now : 1)) {   // 多个任务同时失败时只有一个打
            ESP_LOGW("ina226", "read reg 0x%02x failed (%s) task=%s，上一条告警以来共失败 %u 次", reg,
                     esp_err_to_name(err), pcTaskGetName(nullptr), (unsigned)s_fails.exchange(0));
        }
        return false;
    }
    val = (int16_t)(r_buffer[0] << 8 | r_buffer[1]);
    return true;
}

void INA226::writeRegister16(uint8_t reg, uint16_t val)
{
    uint8_t w_buffer[3] = {0};
    w_buffer[0]         = reg;
    w_buffer[1]         = (val >> 8) & 0xFF;
    w_buffer[2]         = val & 0xFF;
    i2c_master_transmit(i2c_dev_handle_ina226, w_buffer, 3, I2C_MASTER_TIMEOUT_MS);
}
