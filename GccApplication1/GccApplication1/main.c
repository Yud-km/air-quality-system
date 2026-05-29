/*
 * CheckAir.cpp
 *
 * Created: 27/03/2026 10:22:09 CH
 * Author : Admin
 */ 
#define F_CPU 8000000UL
#include <avr/io.h>
#include <util/delay.h>
#include <math.h>
#include <stdio.h>
#include <avr/interrupt.h>
#include <string.h>
#include <stdlib.h>
#include <avr/pgmspace.h>



//====================== khoi tao chan ========================== 
//input
#define DHT_PIN PC6
#define MQ_PIN PA0	// ADC0
#define PM25 PA7	// ADC7
#define btn_Up PD2	//ngat ngoai 0 => doi sang mode auto/man
#define btn_Down PD3//ngat ngoai 1 => doi sang dieu khien toc do quat

//output
#define Led_PM_PIN PA5
#define Buzzer_PIN PD5	// OC1A
#define Fan_PIN PB3		// OC0


#define Led_PIN PC2		// thong bao chuong trinh dang chay

//Oled
#define oled_add 0x3C


//==================== Khai bao bien global =====================
//MQ
#define RL 20000.0 // bien tro Vout
double R0 = 10000.0;// gia tri de hieu chuan CO2 can chinh lai
int const Vref = 5;// dien ap tham chieu


//quat
int duty_Fan = 128;
volatile uint8_t mode = 1;     // 1 = AUTO, 0 = MAN

//coi bao
int duty_Buzzer = 64; // duty ~ 25%


//truyen thong
/*
uart: truyen nhan du lieu voi winform
spi: nap code
i2c: giao tiep voi oled
*/
#define BAUDRATE 9600
#define buffer_size 32


volatile uint32_t millis = 0;
/*
uint8_t temp = 0, humi = 0;
float Co2 = 0;
int dust = 0;
*/

uint8_t g_temp = 0;
uint8_t g_humi = 0;
float g_co2  = 0;
int   g_dust = 0;

float g_vout_mq = 0;
float g_vout_pm = 0;


//============== millis function===============
void Timer2_Init(){
	TCCR2 |= (1 << WGM21);
	OCR2 = 124;
	TCCR2 |= (1 << CS22);
	TIMSK |= (1 << OCIE2);
}

ISR(TIMER2_COMP_vect){
	millis++;
}

uint32_t getMillis(){
	uint32_t t;
	t = millis; 
	return t;
}



//===============DHT function===============
// doc data tu dht voi phuong phap timing
uint8_t Read_DHT(uint8_t *temp, uint8_t *humi){
	uint8_t data[5] = {0};//luu 40 bit
	//gui start
	DDRC |= (1 << DHT_PIN);
	PORTC &= ~(1 << DHT_PIN);
	_delay_ms(20);
	PORTC |= (1 << DHT_PIN);
	_delay_us(30);
	DDRC &= ~(1 << DHT_PIN);
	_delay_us(40);
	
	//ktra phan hoi tu dht
	if(PINC & (1 << DHT_PIN)) return 0;
	while(!(PINC & (1 << DHT_PIN)));
	while(PINC & (1 << DHT_PIN));
	
	//doc 40 bit 
	for(uint8_t i=0; i < 40; i++){
		while(!(PINC & (1 << DHT_PIN)));
		_delay_us(40);
		if(PINC & (1 << DHT_PIN)){
			data[i/8] |= (1 << (7 - (i % 8)));
		}
		while(PINC & (1 << DHT_PIN));
	}
	
	//checksum
	if(data[4] != data[0] + data[1] + data[2] + data[3])
		return 0;
		
	*temp = data[2];
	*humi = data[0];
	return 1;
}


//=================ADC function==================
void ADC_init() {
	ADMUX = (1 << REFS0); // vref = vacc = 5V 
	ADCSRA = (1 << ADEN) | (1 << ADPS2) | (1 << ADPS1) | (1 << ADPS0); //pres = 128
}


//ham doc gia tri adc
uint16_t ADC_Read(uint8_t channel){
	channel &= 0x07;
	ADMUX = (ADMUX & 0xF8) | channel;
	_delay_us(20);
	ADCSRA |= (1 << ADSC);
	while(ADCSRA & (1 << ADSC));
	
	return ADCW;
}


//===============MQ function================
//tinh Rs
float Vout_Co2 = 0.0;
/*
double Get_Rs(){
	double adc_sum = 0;
//	int valid = 0;

	for(int i = 0; i < 5; i++){
		uint16_t adc = ADC_Read(0);

		//if(adc > 50 && adc < 1024){ 
		adc_sum += adc;
			//valid++;
		//}

		_delay_ms(2);
	}

	//if(valid == 0) return 0;
	
	double adc_avg = adc_sum / 5;
	double Vout = adc_avg * Vref / 1024.0;
	if(Vout <= 0.1) Vout = 0.1;
	if(Vout >= 5) Vout = 4.95;
	Vout_Co2 = Vout;

	return RL * (Vref - Vout) / Vout;
}
*/


double Get_Rs(){
	uint16_t adc = ADC_Read(0);
	double Vout = adc * Vref / 1024.0;
	if(Vout <= 0.1) Vout = 0.1;
	if(Vout >= 5) Vout = 4.95;
	Vout_Co2 = Vout;

	return RL * (Vref - Vout) / Vout;
}



//hieu chuan R0 
double Calibrate_R0(){ 
	double rs_sum = 0; 
	int count = 0; 
	for(int i = 0; i < 50; i++){
		 double rs = Get_Rs(); 
		 if(rs > 0 && rs < 100000){ 
			 rs_sum += rs; 
			 count++;
		 } 
		 _delay_ms(200);
	} 
	if(count == 0) return 10000.0; 
	
	rs_sum /= count;
	
	// voi 400ppm CO2
	//R0 = rs_sum * exp(log(116.6020682/414.38)/-2.769034857);
	R0 = rs_sum / pow((414.38 / 116.6020682), (1 / -2.769034857));
	//R0 = rs_sum * 1.58;
	return R0;
}


//tinh CO2
double Get_Co2(){
	double Rs = Get_Rs();

	if(Rs <= 0) return 0;
	if(R0 < 1000) return 0;  

	double ratio = Rs / R0;

	// clamp ratio 
	if(ratio < 0.1) ratio = 0.1;
	if(ratio > 10)  ratio = 10;

	//cong thuc noi suy co2
	double ppm = 116.6020682 * pow(ratio, -2.769034857);

	return ppm;
}



//====================== PM2.5 function ========================
/*
double Get_PM25_voltage(){
	double sum = 0;
	for(int i = 0; i < 5; i++){
		//led on
		PORTA &= ~(1 << Led_PM_PIN);
		_delay_us(280);
		//_delay_us(260);
		
		uint16_t adc_val = ADC_Read(7);
		
		sum += adc_val;
		_delay_us(40);
		
		//led off
		PORTA |= (1 << Led_PM_PIN);
		_delay_us(9680);
	}
	double adc_avg = sum / 5.0;
	return adc_avg * Vref / 1024.0;
}
*/


double Get_PM25_voltage(){
	//led on
	PORTA &= ~(1 << Led_PM_PIN);
	_delay_us(280);
		
	uint16_t adc_val = ADC_Read(7);
	_delay_us(40);
		
	//led off
	PORTA |= (1 << Led_PM_PIN);
	_delay_us(9680);
	
	return adc_val * Vref / 1024.0;
}



//===============OLED function==============
//khoi I2C
//khoi tao
void TWI_init(){
	TWSR = 0x00;//pres = 1
	TWBR = 32; // tao xung scl = 100kHz
	TWCR = (1 << TWEN);
}

//phat tin hieu start
void TWI_Start(){
	TWCR = (1 << TWINT) | (1 << TWSTA) | (1 << TWEN);
	while(!(TWCR & (1 << TWINT)));
}

//phat tin hieu stop
void TWI_Stop(){
	TWCR = (1 << TWINT) | (1 << TWEN) | (1 << TWSTO);
}

//ghi du lieu
void TWI_Write(uint8_t data){
	TWDR = data;
	TWCR = (1 << TWINT) | (1 << TWEN);
	while(!(TWCR & (1 << TWINT)));
}


//Khoi Oled
//gui lenh dieu khien oled
void Oled_Command(uint8_t cmd){
	TWI_Start();
	TWI_Write(oled_add << 1);
	TWI_Write(0x00);
	TWI_Write(cmd);
	TWI_Stop();
}

//gui du lieu hien thi 
void Oled_Data(uint8_t data){
	TWI_Start();
	TWI_Write(oled_add << 1);
	TWI_Write(0x40);
	TWI_Write(data);
	TWI_Stop();
}

//dat vi tri hien thi
void Oled_Setcursor(uint8_t page, uint8_t col){
	Oled_Command(0xB0 + page);
	Oled_Command(0x00 + (col & 0x0f));
	Oled_Command(0x10 + (col >> 4));
}

//xoa man hinh
void Oled_Clear(){
	for(uint8_t p = 0; p < 8; p++){
		Oled_Setcursor(p, 0);
		for(uint8_t c = 0; c < 128; c++){
			Oled_Data(0x00);//tat pixel
		}
	}
}


//font hien thi 
const uint8_t font5x7[96][5] PROGMEM = {
	{0x00,0x00,0x00,0x00,0x00}, // space
	{0x00,0x00,0x5F,0x00,0x00}, // !
	{0x00,0x07,0x00,0x07,0x00}, // "
	{0x14,0x7F,0x14,0x7F,0x14}, // #
	{0x24,0x2A,0x7F,0x2A,0x12}, // $
	{0x23,0x13,0x08,0x64,0x62}, // %
	{0x36,0x49,0x55,0x22,0x50}, // &
	{0x00,0x05,0x03,0x00,0x00}, // '
	{0x00,0x1C,0x22,0x41,0x00}, // (
	{0x00,0x41,0x22,0x1C,0x00}, // )
	{0x14,0x08,0x3E,0x08,0x14}, // *
	{0x08,0x08,0x3E,0x08,0x08}, // +
	{0x00,0x50,0x30,0x00,0x00}, // ,
	{0x08,0x08,0x08,0x08,0x08}, // -
	{0x00,0x60,0x60,0x00,0x00}, // .
	{0x20,0x10,0x08,0x04,0x02}, // /
	{0x3E,0x51,0x49,0x45,0x3E}, // 0
	{0x00,0x42,0x7F,0x40,0x00}, // 1
	{0x42,0x61,0x51,0x49,0x46}, // 2
	{0x21,0x41,0x45,0x4B,0x31}, // 3
	{0x18,0x14,0x12,0x7F,0x10}, // 4
	{0x27,0x45,0x45,0x45,0x39}, // 5
	{0x3C,0x4A,0x49,0x49,0x30}, // 6
	{0x01,0x71,0x09,0x05,0x03}, // 7
	{0x36,0x49,0x49,0x49,0x36}, // 8
	{0x06,0x49,0x49,0x29,0x1E}, // 9
	{0x00,0x36,0x36,0x00,0x00}, // :
	{0x00,0x56,0x36,0x00,0x00}, // ;
	{0x08,0x14,0x22,0x41,0x00}, // <
	{0x14,0x14,0x14,0x14,0x14}, // =
	{0x00,0x41,0x22,0x14,0x08}, // >
	{0x02,0x01,0x51,0x09,0x06}, // ?
	{0x32,0x49,0x79,0x41,0x3E}, // @
	{0x7E,0x11,0x11,0x11,0x7E}, // A
	{0x7F,0x49,0x49,0x49,0x36}, // B
	{0x3E,0x41,0x41,0x41,0x22}, // C
	{0x7F,0x41,0x41,0x22,0x1C}, // D
	{0x7F,0x49,0x49,0x49,0x41}, // E
	{0x7F,0x09,0x09,0x09,0x01}, // F
	{0x3E,0x41,0x49,0x49,0x7A}, // G
	{0x7F,0x08,0x08,0x08,0x7F}, // H
	{0x00,0x41,0x7F,0x41,0x00}, // I
	{0x20,0x40,0x41,0x3F,0x01}, // J
	{0x7F,0x08,0x14,0x22,0x41}, // K
	{0x7F,0x40,0x40,0x40,0x40}, // L
	{0x7F,0x02,0x0C,0x02,0x7F}, // M
	{0x7F,0x04,0x08,0x10,0x7F}, // N
	{0x3E,0x41,0x41,0x41,0x3E}, // O
	{0x7F,0x09,0x09,0x09,0x06}, // P
	{0x3E,0x41,0x51,0x21,0x5E}, // Q
	{0x7F,0x09,0x19,0x29,0x46}, // R
	{0x46,0x49,0x49,0x49,0x31}, // S
	{0x01,0x01,0x7F,0x01,0x01}, // T
	{0x3F,0x40,0x40,0x40,0x3F}, // U
	{0x1F,0x20,0x40,0x20,0x1F}, // V
	{0x3F,0x40,0x38,0x40,0x3F}, // W
	{0x63,0x14,0x08,0x14,0x63}, // X
	{0x07,0x08,0x70,0x08,0x07}, // Y
	{0x61,0x51,0x49,0x45,0x43}, // Z
	{0x00,0x7F,0x41,0x41,0x00}, // [
	{0x02,0x04,0x08,0x10,0x20}, // 
	{0x00,0x41,0x41,0x7F,0x00}, // ]
	{0x04,0x02,0x01,0x02,0x04}, // ^
	{0x40,0x40,0x40,0x40,0x40}, // _
	{0x00,0x01,0x02,0x04,0x00}, // `
	{0x20,0x54,0x54,0x54,0x78}, // a
	{0x7F,0x48,0x44,0x44,0x38}, // b
	{0x38,0x44,0x44,0x44,0x20}, // c
	{0x38,0x44,0x44,0x48,0x7F}, // d
	{0x38,0x54,0x54,0x54,0x18}, // e
	{0x08,0x7E,0x09,0x01,0x02}, // f
	{0x0C,0x52,0x52,0x52,0x3E}, // g
	{0x7F,0x08,0x04,0x04,0x78}, // h
	{0x00,0x44,0x7D,0x40,0x00}, // i
	{0x20,0x40,0x44,0x3D,0x00}, // j
	{0x7F,0x10,0x28,0x44,0x00}, // k
	{0x00,0x41,0x7F,0x40,0x00}, // l
	{0x7C,0x04,0x18,0x04,0x78}, // m
	{0x7C,0x08,0x04,0x04,0x78}, // n
	{0x38,0x44,0x44,0x44,0x38}, // o
	{0x7C,0x14,0x14,0x14,0x08}, // p
	{0x08,0x14,0x14,0x18,0x7C}, // q
	{0x7C,0x08,0x04,0x04,0x08}, // r
	{0x48,0x54,0x54,0x54,0x20}, // s
	{0x04,0x3F,0x44,0x40,0x20}, // t
	{0x3C,0x40,0x40,0x20,0x7C}, // u
	{0x1C,0x20,0x40,0x20,0x1C}, // v
	{0x3C,0x40,0x30,0x40,0x3C}, // w
	{0x44,0x28,0x10,0x28,0x44}, // x
	{0x0C,0x50,0x50,0x50,0x3C}, // y
	{0x44,0x64,0x54,0x4C,0x44}  // z
};
	
	
/*
//hien thi 1 ky tu
void Oled_DrawChar(char c){
	if (c < 32 || c > 127)	c = ' ';
	for(uint8_t i = 0; i < 5; i++){
		Oled_Data(font5x7[c - 32][i]);
	}
	Oled_Data(0x00);
}
*/
void Oled_DrawChar(char c)
{
	if(c < 32 || c > 127)
		c = ' ';

	for(uint8_t i = 0; i < 5; i++)
	{
		uint8_t data = pgm_read_byte(&font5x7[c - 32][i]);

		Oled_Data(data);
	}

	Oled_Data(0x00);
}


//in chuoi 
void Oled_PrintString(char *s){
	while(*s){
		Oled_DrawChar(*s++);
	}
}

//in so nguyen
void Oled_PrintInt(int num){
	char buf[6];
	int i = 0;
	if (num == 0){
		Oled_DrawChar('0');
		return;
	}
	
	while(num > 0){
		buf[i++] = (num % 10) + '0';// tach so tu phai sang
		num /= 10;
	}
	while(i--){
		Oled_DrawChar(buf[i]);// dao nguoc 
	}
} 

//in so float
void Oled_PrintFloat(float value){
	int int_part = (int)(value);
	int decimal = abs((int)((value - int_part) * 100));// lay sau dau phay 2 chu so
	
	Oled_PrintInt(int_part);
	Oled_DrawChar('.');
	Oled_PrintInt(decimal);
}

//khoi tao Oled
void Oled_init(){
	_delay_ms(100);
	Oled_Command(0xae);// off
	Oled_Command(0x20); Oled_Command(0x10);
	Oled_Command(0xB0);
	Oled_Command(0xC8);
	Oled_Command(0x00);
	Oled_Command(0x10);
	Oled_Command(0x40);
	Oled_Command(0x81); Oled_Command(0x7F);
	Oled_Command(0xA1);
	Oled_Command(0xA6);
	Oled_Command(0xA8); Oled_Command(0x3F);
	Oled_Command(0xA4);
	Oled_Command(0xD3); Oled_Command(0x00);
	Oled_Command(0xD5); Oled_Command(0xF0);
	Oled_Command(0xD9); Oled_Command(0x22);
	Oled_Command(0xDA); Oled_Command(0x12);
	Oled_Command(0xDB); Oled_Command(0x20);
	Oled_Command(0x8D); Oled_Command(0x14);
	Oled_Command(0xaf);//on
}


//xoa dong
void Oled_ClearLine(uint8_t page){
	Oled_Setcursor(page, 0);
	for(uint8_t i = 0; i < 128; i++){
		Oled_Data(0x00);
	}
}


//===============Fan function============
void Fan_init(){
	DDRB |= (1 << Fan_PIN);
	TCCR0 |= (1 << WGM00) | (1 << WGM01) | (1 << COM01) | (1 << CS01) | (1 << CS00); // fast PWM, k dao pres = 64;
	
	OCR0 = duty_Fan;
}


void Set_Fan_Speed(uint8_t percent)
{
	if(percent < 0)	percent = 0;
	if (percent > 100) percent = 100;

	// convert % -> 0–255
	duty_Fan = (int)((percent * 255) / 100);
	OCR0 = duty_Fan;
}


//su dung ngat ngoai chon mode 
//mode 
volatile uint8_t flag_mode = 0;
volatile uint8_t flag_speed = 0;
void INT0_init(){
	DDRD &= ~(1 << btn_Up);//input
	PORTD |= (1 << btn_Up); //pullup
	
	MCUCR |= (1 << ISC01);// ngat canh xuong
	MCUCR &= ~(1 << ISC00);
	GICR |= (1 << INT0); //cho phep ngat
}

ISR(INT0_vect){
	flag_mode = 1;
}


//interrupt dieu khien toc do quat
void INT1_init(){
	DDRD &= ~(1 << btn_Down);
	PORTD |= (1 << btn_Down);
	
	MCUCR |= (1 << ISC11);
	MCUCR &= ~(1 << ISC10);
	GICR |= (1 << INT1);
}

ISR(INT1_vect){
	flag_speed = 1;
}



//===============Buzzer function===============
void Buzzer_Init(){
	DDRD |= (1 << Buzzer_PIN);
	
	//output, fast pwm 8 bit, k dao, pres = 8
	TCCR1A |= (1 << WGM10) | (1 << COM1A1);
	TCCR1B |= (1 << WGM12) | (1 << CS11);
	
	OCR1A = 0;
}




//========================== uart function ===========================
//khoi tao set baudrate + khung truyen
void UART_init(unsigned int ubrr){
	UBRRH = (unsigned char)(ubrr >> 8);
	UBRRL = (unsigned char)ubrr;
	UCSRB = (1 << TXEN) | (1 << RXEN) | (1 <<RXCIE);// TX + RX + RX interrupt
	UCSRC = (1 << URSEL) | (1 << UCSZ0) | (1 << UCSZ1);// 8 bit + 1 bit stop
}

//ghi 1 ky tu
void UART_SendChar(unsigned char data){
	while(!(UCSRA & (1 << UDRE)));
	UDR = data;
}

//gui chuoi
void UART_SendString(char *str){
	while(*str){
		UART_SendChar(*str++);
	}
}


char rx_buffer[buffer_size];
uint8_t rx_idx = 0;
volatile uint8_t data_ready = 0;
//================ UART RX INTERRUPT =================
ISR(USART_RXC_vect)
{
	char c = UDR;

	// ket thuc command
	if(c == '\n')
	{
		rx_buffer[rx_idx] = '\0';

		data_ready = 1;

		rx_idx = 0;
	}
	else
	{
		// tranh tran buffer
		if(rx_idx < (buffer_size - 1))
		{
			rx_buffer[rx_idx++] = c;
		}
		else
		{
			rx_idx = 0;
		}
	}
}

//clear buffer - dung sau moi lan doc xong 1 string
void UART_BufferClear(){
	for(uint8_t i = 0; i < rx_idx; i++){
		rx_buffer[i] = 0;
	}
	
	rx_idx = 0;
	data_ready = 0;
}



void Process_Command(char *p){
	//bo qua space va \r
	while(*p == ' ' || *p == '\r') p++; 

	//cmd=> "#mode:x" and "#fan:yy"
	//================mode=================
	if (strncmp(p, "#mode:", 6) == 0){
		int v = atoi(p + 6);
		if (v == 0) mode = 0;
		else mode = 1;
		return;
	}

	//==================fan=================
	if (strncmp(p, "#fan:", 5) == 0){
		int pct = atoi(p + 5);
		if (pct < 0) pct = 0;
		if (pct > 100) pct = 100;
		if (mode == 0) Set_Fan_Speed((uint8_t)pct);
		return;
	}
	
	//================ BUZZER ===============
	if(strncmp(p, "#buzzle:", 8) == 0)
	{
		int v = atoi(p + 8);

		if(v == 0)
		{
			OCR1A = 0;
		}
		else
		{
			// PWM 25%
			// 255 * 25% = 64
			OCR1A = 64;
		}
		return;
	}
}




//======================= Task =================================
void Task_DHT(){
	uint8_t temp_raw, humi_raw;

	if(Read_DHT(&temp_raw, &humi_raw)){
		g_temp = temp_raw ;
		g_humi = humi_raw ;
	}
}


void Task_MQ(){
	g_co2 = Get_Co2() ;
	g_vout_mq = Vout_Co2;

	if(g_co2 > 2000) OCR1A = duty_Buzzer;
	else OCR1A = 0;
}

void Task_PM(){
	double v = Get_PM25_voltage();
	g_vout_pm = v;

	if (v <= 0.6){
		g_dust = 0;
	}
	else if (v <= 3.5){
		g_dust = (0.17 * v - 0.1) * 1000;
		if(g_dust < 0) g_dust = 0;
		if(g_dust > 500) g_dust = 500;
	}
	else{
		g_dust = 999;
	}
}


void Task_OLED(){
	//Oled_Clear();

	// DHT
	Oled_Setcursor(0,0);
	Oled_PrintString("T:");
	Oled_PrintInt(g_temp);
	Oled_PrintString("C  ");

	Oled_PrintString("H:");
	Oled_PrintInt(g_humi);
	Oled_PrintString("%  ");

	// MQ
	Oled_Setcursor(2,0);
	Oled_PrintString("CO2:");
	//Oled_PrintInt(g_co2);
	Oled_PrintFloat(g_co2);
	Oled_PrintString("ppm    ");

	// PM
	Oled_Setcursor(4,0);
	Oled_PrintString("Dust:");
	Oled_PrintInt(g_dust);
	Oled_PrintString("ug     ");

	//Vout
	Oled_Setcursor(2,90);
	//Oled_PrintString("MQ:");
	Oled_PrintFloat(g_vout_mq);
		
	Oled_Setcursor(4,90);
	//Oled_PrintString("PM:");
	Oled_PrintFloat(g_vout_pm);
		

	Oled_Setcursor(6,0);
	if(mode){
		Oled_PrintString("mode: auto");
	}
	else{
		Oled_PrintString("mode: man");
	}
		
	Oled_Setcursor(6, 64);
	Oled_PrintString("duty: ");
	uint8_t percent = (int)duty_Fan * 100 / 255;
	Oled_PrintInt(percent);
	Oled_PrintString("%");
}

char cmd_buffer[buffer_size];
void Task_UART(){
	// ===== NHAN DU LIEU =====	
	if(data_ready){
		cli();
		strcpy(cmd_buffer, rx_buffer);
		data_ready = 0;
		sei();
		Process_Command(cmd_buffer);
		UART_BufferClear();
	}

	//======gui du lieu=======
	char data[50];

	uint8_t percent = (int)OCR0 * 100 / 255;

	uint16_t co2_int = (uint16_t)g_co2;

	snprintf(data,sizeof(data),
		"#data:%u;%u;%u;%u;%u;%u\r\n",
		(uint16_t)g_temp,
		(uint16_t)g_humi,
		co2_int,
		(uint16_t)g_dust,
		(uint16_t)mode,
		(uint16_t)percent);

	UART_SendString(data);
}



void Task_Button(){
	// ---- xu ly nut MODE ----
	if(flag_mode){
		_delay_ms(20);
		if(!(PIND & (1 << btn_Up))){
			if(mode == 0) mode = 1;
			else
			mode = 0;
		}
		
		Oled_ClearLine(6);
		Oled_Setcursor(6,0);
		if(mode){
			Oled_PrintString("mode: auto");
		}
		else{
			Oled_PrintString("mode: man");
		}
		
		Oled_Setcursor(6, 64);
		Oled_PrintString("duty: ");
		uint8_t percent = (int)duty_Fan * 100 / 255;
		Oled_PrintInt(percent);
		Oled_PrintString("%");
		
		flag_mode = 0;
	}
	
	//xu ly nut Speed
	if(flag_speed){
		_delay_ms(20);
		if(!(PIND & (1 << btn_Down))){
			if(mode == 0){
				duty_Fan += 51;
				if(duty_Fan > 255){
					duty_Fan = 0;
				}
			}
		}
		OCR0 = duty_Fan;
		flag_speed = 0;
		
		Oled_ClearLine(6);
		Oled_Setcursor(6,0);
		if(mode){
			Oled_PrintString("mode: auto");
		}
		else{
			Oled_PrintString("mode: man");
		}
		
		Oled_Setcursor(6, 64);
		Oled_PrintString("duty: ");
		uint8_t percent = (int)duty_Fan * 100 / 255;
		Oled_PrintInt(percent);
		Oled_PrintString("%");
	}
}



//======================== main function =======================
int main(void)
{
    /* Replace with your application code */
	//khoi tao cac ngoai vi
	TWI_init();		// I2C
	Oled_init();	// OLED
	INT0_init();	// mode
	INT1_init();	// up(toc do quat)
	Fan_init();		// quat
	Buzzer_Init();	// coi ba
	Timer2_Init();	// millis
	ADC_init();		// ADC

	//led bao khi he hoat dong
	DDRC |= (1 << Led_PIN);//ouput
	PORTC |= (1 << Led_PIN); //led on
	
	//output cho led PM2.5
	DDRA |= (1 << Led_PM_PIN);
	PORTA |= (1 << Led_PM_PIN); 
	
	//tinh ubrr cho uart
	unsigned int ubrr;
	ubrr = (F_CPU / (16UL * BAUDRATE)) - 1;
	UART_init(ubrr);	// baudrate = 9600
	
	// chay khoi dong MQ135
	Oled_Clear();
	Oled_Setcursor(0,0);
	Oled_PrintString("Calibrating MQ...");
	_delay_ms(500);
	R0 = Calibrate_R0();
	Oled_Setcursor(2,0);
	Oled_PrintString("R0:");
	Oled_PrintFloat(R0);
	_delay_ms(1000);
	
	Oled_Clear();
	sei();//ngat toan cuc
	_delay_ms(50);
	

	// CHI CHAY 1 TASK TAI 1 THOI DIEM
	// KHONG CHO CAC TASK TRUNG NHAU
	while(1)
	{
		static uint32_t last_time = 0;
		static uint8_t task_step = 0;

		// button duoc check lien tuc
		Task_Button();

		// moi 500ms chay 1 task
		if(getMillis() - last_time >= 500)
		{
			last_time = getMillis();

			switch(task_step)
			{
				//================ TASK DHT =================
				case 0:
				{
					Task_DHT();
					break;
				}

				//================ TASK PM2.5 =================
				case 1:
				{
					Task_PM();
					break;
				}

				//================ TASK MQ135 =================
				case 2:
				{
					Task_MQ();
					break;
				}

				//================ TASK OLED =================
				case 3:
				{
					Task_OLED();
					break;
				}
				
				//================ TASK UART =================
				case 4:
				{
					Task_UART();
					break;
				}
			}

			// task tiep theo
			task_step++;

			if(task_step >= 5)
			{
				task_step = 0;
			}
		}
	}


/*
	while(1){
		Task_UART();
		_delay_ms(1000);
	}
	
*/
}

	
	
	
	
	
	


