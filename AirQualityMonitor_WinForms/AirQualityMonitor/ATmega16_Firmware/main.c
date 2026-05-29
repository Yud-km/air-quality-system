/*
 * ============================================================
 *  AIR QUALITY MONITOR – ATmega16 Firmware
 *  Sensors : MQ135 (CO2/ADC), DHT11 (Temp+Hum), GP2Y1010AU0F (PM2.5/ADC)
 *  Outputs : Fan PWM (Timer1 OC1B / PD5), Buzzer (PD7)
 *  UART    : 9600 baud, 8N1
 *
 *  UART TX frame (every ~2 s):
 *    PM:xx.x,CO:xxxx,T:xx.x,H:xx.x\r\n
 *
 *  UART RX commands (from PC WinForms):
 *    FAN:1   – Fan ON
 *    FAN:0   – Fan OFF
 *    SPD:xx  – Fan speed 0-100 (maps to PWM duty)
 *    BUZ:1   – Buzzer ON
 *    BUZ:0   – Buzzer OFF
 *
 *  Pin map (ATmega16, 40-pin DIP):
 *    PA0 – MQ135 analog output (ADC0)
 *    PA1 – PM2.5 sensor ILED (via 150 Ω) + PA2 (ADC2) for Vo
 *    PA2 – PM2.5 analog output (ADC2)
 *    PD2 – DHT11 data (single-wire)
 *    PD5 – Fan PWM output (OC1B – Timer1 Fast PWM)
 *    PD7 – Buzzer (active buzzer, HIGH = ON)
 *    PD0 – UART RX
 *    PD1 – UART TX
 * ============================================================
 */

#define F_CPU 8000000UL   // Change to 16000000UL if using 16 MHz crystal

#include <avr/io.h>
#include <avr/interrupt.h>
#include <util/delay.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdbool.h>

/* ── Baud rate ───────────────────────────────────────────────────────────── */
#define BAUD       9600
#define UBRR_VAL   ((F_CPU / (16UL * BAUD)) - 1)

/* ── Pins ────────────────────────────────────────────────────────────────── */
#define DHT11_PORT  PORTD
#define DHT11_DDR   DDRD
#define DHT11_PIN   PIND
#define DHT11_BIT   PD2

#define FAN_BIT     PD5     /* OC1B */
#define BUZZER_BIT  PD7

#define PM25_ILED_PORT  PORTA
#define PM25_ILED_DDR   DDRA
#define PM25_ILED_BIT   PA1   /* LED drive for dust sensor */

/* ── ADC channels ────────────────────────────────────────────────────────── */
#define ADC_MQ135   0
#define ADC_PM25    2

/* ── Timer1 Fast PWM top (ICR1 = 255 → 8-bit like, ~31 kHz @ 8 MHz) ─────── */
#define PWM_TOP     255

/* ────────────────────────────────────────────────────────────────────────── */
/*  GLOBALS                                                                   */
/* ────────────────────────────────────────────────────────────────────────── */
volatile char rx_buf[64];
volatile uint8_t rx_head = 0;
volatile bool    rx_ready = false;

bool fan_on     = false;
bool buzzer_on  = false;
uint8_t fan_spd = 50;   /* 0-100 % */

/* Sensor readings (updated each cycle) */
float pm25_ug   = 0.0f;
float co2_ppm   = 0.0f;
float dht_temp  = 0.0f;
float dht_hum   = 0.0f;

/* ────────────────────────────────────────────────────────────────────────── */
/*  UART                                                                      */
/* ────────────────────────────────────────────────────────────────────────── */
void uart_init(void)
{
    UBRRH = (uint8_t)(UBRR_VAL >> 8);
    UBRRL = (uint8_t)(UBRR_VAL);
    UCSRB = (1 << RXEN) | (1 << TXEN) | (1 << RXCIE);  /* RX int enabled */
    UCSRC = (1 << URSEL) | (1 << UCSZ1) | (1 << UCSZ0); /* 8N1 */
}

void uart_putc(char c)
{
    while (!(UCSRA & (1 << UDRE)));
    UDR = c;
}

void uart_puts(const char *s)
{
    while (*s) uart_putc(*s++);
}

/* Redirect printf to UART */
static int uart_stream_putc(char c, FILE *stream)
{
    (void)stream;
    if (c == '\n') uart_putc('\r');
    uart_putc(c);
    return 0;
}
static FILE uart_stdout = FDEV_SETUP_STREAM(uart_stream_putc, NULL, _FDEV_SETUP_WRITE);

/* RX interrupt – collect chars until '\n' */
ISR(USART_RXC_vect)
{
    char c = UDR;
    if (c == '\n' || c == '\r') {
        if (rx_head > 0) {
            rx_buf[rx_head] = '\0';
            rx_head = 0;
            rx_ready = true;
        }
    } else if (rx_head < 62) {
        rx_buf[rx_head++] = c;
    }
}

/* ────────────────────────────────────────────────────────────────────────── */
/*  ADC                                                                       */
/* ────────────────────────────────────────────────────────────────────────── */
void adc_init(void)
{
    /* AVcc reference, ADC0 default */
    ADMUX  = (1 << REFS0);
    /* Enable ADC, prescaler /64 → ~125 kHz @ 8 MHz */
    ADCSRA = (1 << ADEN) | (1 << ADPS2) | (1 << ADPS1);
}

uint16_t adc_read(uint8_t ch)
{
    ADMUX = (ADMUX & 0xE0) | (ch & 0x1F);
    ADCSRA |= (1 << ADSC);
    while (ADCSRA & (1 << ADSC));
    return ADC;
}

/* ────────────────────────────────────────────────────────────────────────── */
/*  PWM (Timer1 Fast PWM on OC1B)                                            */
/* ────────────────────────────────────────────────────────────────────────── */
void pwm_init(void)
{
    DDRD |= (1 << FAN_BIT);
    /* Fast PWM, TOP=ICR1, non-inverting OC1B */
    TCCR1A = (1 << COM1B1) | (1 << WGM11);
    TCCR1B = (1 << WGM13) | (1 << WGM12) | (1 << CS10); /* no prescaler */
    ICR1   = PWM_TOP;
    OCR1B  = 0;   /* start off */
}

void pwm_set_duty(uint8_t percent)
{
    /* percent 0-100 mapped to 0-PWM_TOP */
    OCR1B = (uint16_t)((uint32_t)percent * PWM_TOP / 100);
}

/* ────────────────────────────────────────────────────────────────────────── */
/*  BUZZER                                                                    */
/* ────────────────────────────────────────────────────────────────────────── */
void buzzer_init(void)
{
    DDRD |= (1 << BUZZER_BIT);
    PORTD &= ~(1 << BUZZER_BIT);
}

void buzzer_set(bool on)
{
    if (on) PORTD |=  (1 << BUZZER_BIT);
    else    PORTD &= ~(1 << BUZZER_BIT);
}

/* ────────────────────────────────────────────────────────────────────────── */
/*  DHT11                                                                     */
/* ────────────────────────────────────────────────────────────────────────── */
#define DHT_SET_OUTPUT()  (DHT11_DDR  |=  (1 << DHT11_BIT))
#define DHT_SET_INPUT()   (DHT11_DDR  &= ~(1 << DHT11_BIT))
#define DHT_HIGH()        (DHT11_PORT |=  (1 << DHT11_BIT))
#define DHT_LOW()         (DHT11_PORT &= ~(1 << DHT11_BIT))
#define DHT_READ()        (DHT11_PIN  &   (1 << DHT11_BIT))

static bool dht11_read(float *temp, float *hum)
{
    uint8_t data[5] = {0};

    /* Start signal: pull low ≥18 ms, then release */
    DHT_SET_OUTPUT();
    DHT_LOW();
    _delay_ms(20);
    DHT_HIGH();
    _delay_us(40);
    DHT_SET_INPUT();

    /* Wait for sensor response (low ~80 µs, then high ~80 µs) */
    uint16_t timeout = 10000;
    while (DHT_READ() && timeout--);
    if (!timeout) return false;
    timeout = 10000;
    while (!DHT_READ() && timeout--);
    if (!timeout) return false;
    timeout = 10000;
    while (DHT_READ() && timeout--);
    if (!timeout) return false;

    /* Read 40 bits */
    for (uint8_t byte = 0; byte < 5; byte++) {
        for (uint8_t bit = 7; bit != 255; bit--) {
            /* Wait for rising edge */
            timeout = 10000;
            while (!DHT_READ() && timeout--);
            if (!timeout) return false;
            _delay_us(40);
            if (DHT_READ())
                data[byte] |= (1 << bit);
            /* Wait for line to go low */
            timeout = 10000;
            while (DHT_READ() && timeout--);
            if (!timeout) return false;
        }
    }

    /* Checksum */
    if ((uint8_t)(data[0] + data[1] + data[2] + data[3]) != data[4])
        return false;

    *hum  = data[0] + data[1] * 0.1f;
    *temp = data[2] + data[3] * 0.1f;
    return true;
}

/* ────────────────────────────────────────────────────────────────────────── */
/*  PM2.5 – Sharp GP2Y1010AU0F                                               */
/*  Pulse ILED 0.32 ms, sample Vo at ~0.28 ms after rising edge             */
/* ────────────────────────────────────────────────────────────────────────── */
void pm25_init(void)
{
    PM25_ILED_DDR  |= (1 << PM25_ILED_BIT);
    PM25_ILED_PORT &= ~(1 << PM25_ILED_BIT); /* LED off */
}

static float pm25_read_ug(void)
{
    /* Turn LED on */
    PM25_ILED_PORT |= (1 << PM25_ILED_BIT);
    _delay_us(280);

    uint16_t raw = adc_read(ADC_PM25);

    _delay_us(40);
    /* Turn LED off */
    PM25_ILED_PORT &= ~(1 << PM25_ILED_BIT);
    _delay_us(9680); /* total period ~10 ms */

    /* Vout in volts (Vref = 5 V, 10-bit ADC) */
    float voltage = raw * (5.0f / 1023.0f);

    /* Datasheet linear approximation:
       Dust density (µg/m³) = (Vout - 0.9) / 5e-3
       Clamp to 0 */
    float dust = (voltage - 0.9f) / 0.005f;
    if (dust < 0.0f) dust = 0.0f;
    return dust;
}

/* ────────────────────────────────────────────────────────────────────────── */
/*  MQ-135 CO2 Estimation                                                    */
/*  Simple linear approximation tuned for 400-2000 ppm range                */
/* ────────────────────────────────────────────────────────────────────────── */
#define MQ135_RL        10.0f   /* load resistance kΩ */
#define MQ135_R0        76.63f  /* sensor R0 in clean air (calibrate!) */

static float mq135_read_ppm(void)
{
    uint16_t raw = adc_read(ADC_MQ135);
    float voltage = raw * (5.0f / 1023.0f);

    /* Rs = RL * (Vcc - Vout) / Vout */
    float rs = MQ135_RL * (5.0f - voltage) / voltage;
    float ratio = rs / MQ135_R0;  /* Rs/Ro */

    /* CO2: ppm = a * (Rs/Ro)^b  (from curve-fitting datasheet graph) */
    /* Approximate: ppm = 116.6020682 * ratio^(-2.769034857)  */
    /* Simplified for MCU – use lookup or polynomial */
    /* Quick linear fallback: */
    float ppm = 400.0f + (1.0f - ratio) * 1600.0f;
    if (ppm < 400.0f)  ppm = 400.0f;
    if (ppm > 5000.0f) ppm = 5000.0f;
    return ppm;
}

/* ────────────────────────────────────────────────────────────────────────── */
/*  COMMAND PARSER                                                            */
/* ────────────────────────────────────────────────────────────────────────── */
static void parse_command(const char *cmd)
{
    if (strncmp(cmd, "FAN:1", 5) == 0) {
        fan_on = true;
        pwm_set_duty(fan_spd);
    }
    else if (strncmp(cmd, "FAN:0", 5) == 0) {
        fan_on = false;
        pwm_set_duty(0);
    }
    else if (strncmp(cmd, "SPD:", 4) == 0) {
        uint8_t spd = (uint8_t)atoi(cmd + 4);
        if (spd > 100) spd = 100;
        fan_spd = spd;
        if (fan_on) pwm_set_duty(fan_spd);
    }
    else if (strncmp(cmd, "BUZ:1", 5) == 0) {
        buzzer_on = true;
        buzzer_set(true);
    }
    else if (strncmp(cmd, "BUZ:0", 5) == 0) {
        buzzer_on = false;
        buzzer_set(false);
    }
}

/* ────────────────────────────────────────────────────────────────────────── */
/*  MAIN                                                                      */
/* ────────────────────────────────────────────────────────────────────────── */
int main(void)
{
    /* Init peripherals */
    uart_init();
    adc_init();
    pwm_init();
    buzzer_init();
    pm25_init();

    /* Redirect printf */
    stdout = &uart_stdout;

    sei();   /* Global interrupt enable */

    /* Give sensors time to warm up */
    _delay_ms(1500);

    /* Counters for non-blocking timing */
    uint16_t sample_tick = 0;  /* increment every ~10 ms */

    while (1)
    {
        /* ── Handle incoming PC command ───────────────────────────────── */
        if (rx_ready) {
            rx_ready = false;
            parse_command((const char *)rx_buf);
        }

        /* ── Sample and transmit every ~2 s (200 × 10 ms) ──────────── */
        if (sample_tick >= 200)
        {
            sample_tick = 0;

            /* Read PM2.5 */
            pm25_ug = pm25_read_ug();

            /* Read CO2 (MQ135) */
            co2_ppm = mq135_read_ppm();

            /* Read DHT11 */
            float t = dht_temp, h = dht_hum;
            if (dht11_read(&t, &h)) {
                dht_temp = t;
                dht_hum  = h;
            }
            /* else: keep last valid reading */

            /* Auto-alert: buzzer if PM2.5 > 75 µg/m³ or CO2 > 2000 ppm */
            if (!buzzer_on) {   /* only if PC has not manually set it */
                if (pm25_ug > 75.0f || co2_ppm > 2000.0f) {
                    buzzer_set(true);
                } else {
                    buzzer_set(false);
                }
            }

            /* Transmit frame: PM:xx.x,CO:xxxx,T:xx.x,H:xx.x */
            printf("PM:%.1f,CO:%.0f,T:%.1f,H:%.1f\r\n",
                   pm25_ug, co2_ppm, dht_temp, dht_hum);
        }

        /* 10 ms tick */
        _delay_ms(10);
        sample_tick++;
    }

    return 0;
}
