#pragma once
#include <stddef.h>
#include <stdint.h>

// Portable UTF-8/BMP decoder and keystroke planner. Initial/final IME state is Chinese.
namespace pinyin {
constexpr bool next(const uint8_t* data,size_t size,size_t& pos,uint16_t& code) {
  if(pos>=size)return false;
  uint8_t a=data[pos++];uint32_t c=a;
  if(a>=0xc2 && a<=0xdf) {
    if(pos>=size || (data[pos]&0xc0)!=0x80)return false;
    c=((a&31)<<6)|(data[pos++]&63);
  } else if(a>=0xe0 && a<=0xef) {
    if(size-pos<2 || (data[pos]&0xc0)!=0x80 || (data[pos+1]&0xc0)!=0x80)return false;
    c=((a&15)<<12)|((data[pos]&63)<<6)|(data[pos+1]&63);pos+=2;
    if(c<0x800)return false;
  } else if(a>=0x80)return false;
  if(c>=0xfffe || (c>=0xd800 && c<=0xdfff) || (c<32 && c!=9 && c!=10) || (c>=0x7f && c<=0x9f))return false;
  code=uint16_t(c);return true;
}
constexpr bool valid(const uint8_t* data,size_t size) {
  if(!size || size>16384)return false;
  size_t pos=0;uint16_t c=0;
  while(pos<size)if(!next(data,size,pos,c))return false;
  return true;
}
constexpr void vuc(uint16_t code,char* output) {
  const char digits[]="0123456789abcdef";
  output[0]='v';output[1]='u';output[2]='c';
  for(unsigned i=0;i<4;i++)output[3+i]=digits[(code>>(12-i*4))&15];
  output[7]=0;
}
template<class Direct,class Unicode,class Toggle>
constexpr bool plan(const uint8_t* data,size_t size,Direct direct,Unicode unicode,Toggle toggle) {
  if(!valid(data,size))return false;
  size_t pos=0;uint16_t code=0;bool english=false;
  while(pos<size) {
    if(!next(data,size,pos,code))return false;
    bool ascii=code<128;
    if(ascii!=english){if(!toggle(ascii))return false;english=ascii;}
    if(ascii){if(!direct(uint8_t(code)))return false;}
    else if(!unicode(code))return false;
  }
  return !english || toggle(false);
}
}
