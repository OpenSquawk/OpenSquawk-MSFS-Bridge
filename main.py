from SimConnect import *
sm = SimConnect()
aq = AircraftRequests(sm, _time=2000)
altitude = aq.get("FUEL_TOTAL_QUANTITY")
print(altitude)
