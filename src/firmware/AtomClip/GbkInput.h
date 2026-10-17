#pragma once
#include <stddef.h>
#include <stdint.h>

// The clients supply strict, lossless GBK. No conversion table on the board.
namespace gbk {
constexpr bool valid(const uint8_t* bytes,size_t length) {
  if(!length || length>16384)return false;
  for(size_t i=0;i<length;i++) {
    uint8_t a=bytes[i];
    if(a<128) { if((a<32 && a!=9 && a!=10) || a==127)return false; }
    else if(a<0x81 || a>0xfe || ++i>=length || bytes[i]<0x40 || bytes[i]>0xfe || bytes[i]==0x7f)return false;
  }
  return true;
}
template<class Direct,class Encoded,class Ready>
constexpr bool plan(const uint8_t* bytes,size_t length,Direct direct,Encoded encoded,Ready ready) {
  if(!valid(bytes,length))return false;
  for(size_t i=0;i<length;i++) {
    if(!ready())return false;
    uint16_t value=bytes[i];
    if(value<128) { if(!direct(uint8_t(value)))return false; }
    else { value=uint16_t((value<<8)|bytes[++i]);if(!encoded(value))return false; }
  }
  return true;
}
// A stop request is checked at character boundaries. Releasing Alt mid-number
// would commit a different character, so waits inside a code are indivisible.
template<class Stroke,class Wait>
constexpr bool emit(uint16_t value,uint8_t down,uint8_t gap,uint8_t before,uint8_t after,Stroke stroke,Wait wait) {
  char digits[5]={};unsigned count=0;
  do {digits[count++]=char(value%10);value=uint16_t(value/10);}while(value);
  if(!stroke(0xe2,true))return false;wait(before);
  while(count) {
    uint8_t digit=uint8_t(digits[--count]),usage=digit?uint8_t(0x58+digit):uint8_t(0x62);
    if(!stroke(usage,true))return false;wait(down);
    if(!stroke(usage,false))return false;wait(gap);
  }
  if(!stroke(0xe2,false))return false;wait(after);
  return true;
}
}
