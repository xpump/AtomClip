#pragma once
#include <stdint.h>
#include <stddef.h>
#include <vector>
#include <functional>
#include <algorithm>

namespace ac {
constexpr size_t MAX_PAYLOAD = 16512;
constexpr uint8_t MAGIC[4] = {'A','C','B','1'};
using Bytes = std::vector<uint8_t>;
inline uint32_t le32(const uint8_t* b) {
  return uint32_t(b[0]) | uint32_t(b[1])<<8 | uint32_t(b[2])<<16 | uint32_t(b[3])<<24;
}
inline void put32(uint8_t* b,uint32_t v) { for(int i=0;i<4;i++) b[i]=v>>(8*i); }
inline uint32_t crc(const uint8_t* b, size_t n) {
  uint32_t c=0xffffffff;
  while(n--) { c^=*b++; for(int i=0;i<8;i++) c=(c>>1)^((c&1)?0xedb88320u:0); }
  return ~c;
}
inline Bytes frame(uint8_t kind,const uint8_t* payload,size_t n) {
  Bytes b(13+n); std::copy(MAGIC,MAGIC+4,b.begin()); b[4]=kind;put32(b.data()+5,n);
  if(n) std::copy(payload,payload+n,b.begin()+9);
  put32(b.data()+9+n,crc(b.data()+4,5+n));return b;
}
class Parser {
  Bytes buf;
  uint32_t last=0;
  bool management;
public:
  explicit Parser(bool usbManagement=false):management(usbManagement) { buf.reserve(MAX_PAYLOAD+13); }
  void reset() { buf.clear(); }
  void expire(uint32_t now) { if(!buf.empty() && uint32_t(now-last)>3000) reset(); }
  void feed(const uint8_t* input,size_t n,uint32_t now,const std::function<void(Bytes&&)>& emit) {
    last=now;
    while(n--) {
      buf.push_back(*input++);
      // resynchronise on noise, invalid lengths or failed CRC
      for(;;) {
        if(buf.size()<4) break;
        if(!std::equal(MAGIC,MAGIC+4,buf.begin())) { buf.erase(buf.begin());continue; }
        if(buf.size()<9) break;
        uint32_t len=le32(buf.data()+5);
        if(len>MAX_PAYLOAD || buf[4]<1 || (buf[4]>6 && !(management && buf[4]==7))) {buf.erase(buf.begin());continue;}
        if(buf.size()<13+len) break;
        if(crc(buf.data()+4,5+len)!=le32(buf.data()+9+len)) {buf.erase(buf.begin());continue;}
        Bytes ready;ready.swap(buf);buf.reserve(MAX_PAYLOAD+13);emit(std::move(ready));break;
      }
    }
  }
};
}
