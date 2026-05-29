# Giám sát chất lượng không khí
## ATmega16 + MQ135 + DHT11 + GP2Y1010 + Fan PWM + Buzzer — WinForms C#

---

## 1. CẤU TRÚC PROJECT

```
AirQualityMonitor/
├── GiaoDien.sln                  ← Mở file này bằng Visual Studio
├── AirQualityMonitor.csproj
├── Form1.cs                      ← Toàn bộ UI + logic giao tiếp UART
├── Program.cs
├── Properties/
│   └── AssemblyInfo.cs
└── ATmega16_Firmware/
    └── main.c                    ← Nạp vào ATmega16 bằng Atmel Studio
```

---

## 2. YÊU CẦU PHẦN MỀM

| Thành phần | Phiên bản |
|---|---|
| Visual Studio | 2019 / 2022 |
| .NET Framework | 4.8 |
| Atmel Studio | 7.x (để build firmware) |
| Driver USB-UART | CP2102 / CH340 / FT232 |

---

## 3. SƠ ĐỒ ĐẤU NỐI PHẦN CỨNG

```
ATmega16 (40-pin DIP)
┌──────────────────────────────────┐
│ PA0 ──── MQ135 AOUT             │   (ADC0)
│ PA1 ──── GP2Y1010 ILED (150 Ω) │   LED drive
│ PA2 ──── GP2Y1010 Vo            │   (ADC2)
│ PD0 ──── RX ←── USB-UART TX    │
│ PD1 ──── TX ──→ USB-UART RX    │
│ PD2 ──── DHT11 DATA            │   (Single-wire)
│ PD5 ──── Fan PWM (OC1B)        │   MOSFET → Fan
│ PD7 ──── Buzzer (+)            │   Active buzzer
│ VCC ──── +5V                   │
│ GND ──── GND                   │
│ XTAL1/2 ─ 8 MHz crystal        │
└──────────────────────────────────┘

MQ135:
  VCC → +5V
  GND → GND
  AOUT → PA0
  (dùng biến trở 10kΩ trên board module để chỉnh R0)

GP2Y1010AU0F (Dust Sensor):
  Pin 1 (V-LED) → PA1 qua điện trở 150 Ω → +5V
  Pin 2 (LED-GND) → Tụ 220µF → GND (và GND trực tiếp)
  Pin 3 (LED) → PA1 (điều khiển LED)
  Pin 4 (GND) → GND
  Pin 5 (Vo) → PA2
  Pin 6 (VCC) → +5V

DHT11:
  VCC → +5V
  GND → GND
  DATA → PD2 + điện trở kéo lên 4.7 kΩ lên +5V

Fan PWM:
  PD5 → Gate MOSFET (IRF520/IRLZ44N)
  Drain MOSFET → Fan (-)
  Fan (+) → +12V (hoặc +5V tùy quạt)
  Diode flyback song song với Fan

Buzzer (active):
  PD7 → Base transistor NPN (2N2222) qua 1 kΩ
  Collector → Buzzer (+) → +5V
  Emitter → GND
```

---

## 4. BUILD & NẠP FIRMWARE

1. Mở **Atmel Studio 7**
2. File → Open → `ATmega16_Firmware/main.c` (hoặc tạo project GCC C mới, thêm file)
3. Chỉnh `F_CPU` trong `main.c` cho đúng tần số thạch anh của bạn:
   - `#define F_CPU 8000000UL` (8 MHz)
   - `#define F_CPU 16000000UL` (16 MHz)
4. **Hiệu chỉnh MQ135**: Để sensor ở không khí sạch 20 phút, đọc điện áp ADC, tính R0:
   ```
   Rs_clean = RL * (5.0 - Vout) / Vout
   R0 = Rs_clean / 3.6   (hệ số chuẩn từ datasheet MQ135)
   ```
   Sau đó gán `#define MQ135_R0 <giá_trị_tính_được>`
5. Build → Flash (AVRISP mkII / USBasp / Arduino as ISP)
6. Fuse bits: CKSEL = External Crystal 8 MHz (hoặc 16 MHz)

---

## 5. SỬ DỤNG GIAO DIỆN WINFORMS

### Kết nối phần cứng:
1. Cắm module USB-UART vào máy tính
2. Mở ứng dụng → Chọn **Cổng COM** và **Baud rate = 9600**
3. Nhấn **Kết nối** → Đèn xanh "Đã kết nối" xuất hiện
4. Dữ liệu PM2.5, CO2, Nhiệt độ, Độ ẩm tự động cập nhật

### Chế độ Demo (không cần phần cứng):
- Nhấn **▶ Chế độ Demo** → Sinh dữ liệu mô phỏng ngẫu nhiên, xem biểu đồ thời gian thực

### Điều khiển quạt:
- **Thủ công**: Nhấn BẬT/TẮT QUẠT + kéo thanh tốc độ → gửi `FAN:1`/`FAN:0`, `SPD:xx`
- **Tự động (PM2.5)**: Quạt tự bật khi PM2.5 vượt ngưỡng cài đặt

### Điều khiển còi:
- Nhấn **BẬT CÒI** → gửi `BUZ:1` → ATmega16 bật buzzer

### Nhật ký & Xuất dữ liệu:
- **Xuất CSV**: Lưu log dạng bảng `.csv` mở bằng Excel
- **Xuất HTML**: Lưu báo cáo dạng web `.html`
- **Xóa log**: Xóa bảng dữ liệu hiện tại

---

## 6. GIAO THỨC UART

### PC ← MCU (mỗi ~2 giây):
```
PM:35.2,CO:520,T:28.5,H:65.3\r\n
```

### PC → MCU (lệnh điều khiển):
```
FAN:1       ← Bật quạt
FAN:0       ← Tắt quạt
SPD:75      ← Đặt tốc độ quạt 75%
BUZ:1       ← Bật còi
BUZ:0       ← Tắt còi
```

---

## 7. NGÕ VÀO GIAO DIỆN HỖ TRỢ NHIỀU ĐỊNH DẠNG

Giao diện tự nhận diện các định dạng:
```
PM:35.2,CO:520,T:28.5,H:65.3        (dấu phẩy)
$PM:35.2;CO:520;T:28.5;H:65.3#      (dấu chấm phẩy, bọc $...#)
{"pm":35.2,"co2":520,"t":28.5,"h":65.3}   (JSON)
```

---

## 8. MÀU SẮC CẢNH BÁO CHẤT LƯỢNG KHÔNG KHÍ

| PM2.5 (µg/m³) | Mức | Màu |
|---|---|---|
| 0 – 12 | Tốt | Xanh lá |
| 12 – 35 | Trung bình | Cam |
| 35 – 55 | Kém | Cam đậm |
| > 55 | Nguy hiểm | Đỏ |

| CO₂ (ppm) | Mức | Màu |
|---|---|---|
| < 400 | Bình thường | Xanh lá |
| 400 – 1000 | Trung bình | Cam |
| > 1000 | Kém | Đỏ |

---

*Đề tài: Ứng dụng máy tính trong đo lường và điều khiển*
