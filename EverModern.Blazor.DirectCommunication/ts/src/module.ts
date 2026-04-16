import "./ice-utilities.js";
import "./rtc-connection.js";
import "./rtc-connection-factory.js";
import { rtcConnectionFactory } from "./rtc-connection-factory.js";

(window as any).rtcConnectionFactory = rtcConnectionFactory; 